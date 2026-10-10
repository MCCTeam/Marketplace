using DMCBK.Core;
using DMCBK.Core.Commands;
using Umpk.Data.Java;

namespace DMCBK.OfficialPlugins.McpServer;

/// <summary>Session-scoped read tools: chunks, raycast, players, history, catalogs, blocks.</summary>
public sealed partial class McpCapabilities
{
    /// <summary>mcc_chunk_status: point query reports whether the column holding the coordinate is loaded; without coordinates a 3x3 grid around the player is reported.</summary>
    public async Task<McpResult> GetChunkStatusAsync(double? x, double? y, double? z, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.SessionStatus);
        if (!gate.Success)
            return gate;

        try
        {
            if (x.HasValue || y.HasValue || z.HasValue)
            {
                if (!x.HasValue || !y.HasValue || !z.HasValue)
                {
                    return McpResult.Fail(
                        McpErrorCodes.InvalidArgs, "Provide all of x, y and z, or none of them.");
                }

                var pos = new Umpk.Geometry.BlockPos(
                    (int)Math.Floor(x.Value), (int)Math.Floor(y.Value), (int)Math.Floor(z.Value));
                BlockInfo block = await _context.Game.World.GetBlockAsync(pos, ct).ConfigureAwait(false);
                return McpResult.Ok(new
                {
                    x = pos.X,
                    y = pos.Y,
                    z = pos.Z,
                    chunkX = pos.X >> 4,
                    chunkZ = pos.Z >> 4,
                    loaded = block.ChunkLoaded,
                    fullyLoaded = (bool?)null,
                    fullyLoadedNote = "Per-column fully-loaded state has no counterpart in the new world model.",
                });
            }

            Umpk.Client.Snapshots.ChunkStatusGrid grid = await _context.Game.World
                .GetChunkStatusAsync(3, 3, ct).ConfigureAwait(false);
            return McpResult.Ok(new
            {
                centerChunkX = grid.CenterChunkX,
                centerChunkZ = grid.CenterChunkZ,
                columns = grid.Columns,
                rows = grid.Rows,
                loadedCount = grid.LoadedCount,
                loaded = grid.Loaded.ToList(),
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_raycast_block: first non-air block along the view, or a clean miss.</summary>
    public async Task<McpResult> RaycastBlockAsync(double maxDistance, bool includeNeighbors, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.EntityWorld);
        if (!gate.Success)
            return gate;

        if (maxDistance is <= 0 or > 128)
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "maxDistance must be within (0, 128].");

        try
        {
            BlockRaycastHit? hit = await _context.Game.World
                .RaycastAsync(maxDistance, includeNeighbors, ct).ConfigureAwait(false);
            if (hit is null)
                return McpResult.Ok(new { hit = false });

            PlayerPose pose = await _context.Game.Movement.GetPoseAsync(ct).ConfigureAwait(false);
            return McpResult.Ok(new
            {
                hit = true,
                playerLocation = Loc(pose.Position.X, pose.Position.Y, pose.Position.Z),
                location = new { x = hit.Position.X, y = hit.Position.Y, z = hit.Position.Z },
                block = new { id = hit.BlockId, stateId = hit.StateId },
                face = hit.Face.ToString(),
                distance = Math.Round(hit.Distance, 2),
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_players_list: tab-list names.</summary>
    public async Task<McpResult> GetPlayersListAsync(CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.SessionStatus);
        if (!gate.Success)
            return gate;

        try
        {
            TabListSnapshot tab = await _context.Game.Player.GetTabListAsync(ct).ConfigureAwait(false);
            return McpResult.Ok(new
            {
                count = tab.Entries.Count,
                players = tab.Entries.Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_players_detailed: tab-list entries joined with tracked entity coordinates when available.</summary>
    public async Task<McpResult> GetPlayersDetailedAsync(bool includeSelf, bool includeCoordinates, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.SessionStatus);
        if (!gate.Success)
            return gate;

        try
        {
            TabListSnapshot tab = await _context.Game.Player.GetTabListAsync(ct).ConfigureAwait(false);
            IReadOnlyList<EntitySnapshot> entities;
            try
            {
                entities = includeCoordinates
                    ? await _context.Game.Entities.AllAsync(ct).ConfigureAwait(false)
                    : [];
            }
            catch (DmcbkFeatureDisabledException)
            {
                entities = [];
                includeCoordinates = false;
            }

            string self = await UsernameAsync(ct).ConfigureAwait(false);
            var rows = new List<object>();
            foreach (TabListEntryInfo entry in tab.Entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!includeSelf && string.Equals(entry.Name, self, StringComparison.OrdinalIgnoreCase))
                    continue;

                EntitySnapshot? match = null;
                if (includeCoordinates)
                {
                    match = entities.FirstOrDefault(e =>
                        string.Equals(e.PlayerName, entry.Name, StringComparison.OrdinalIgnoreCase));
                }

                rows.Add(new
                {
                    name = entry.Name,
                    uuid = entry.Uuid,
                    ping = entry.Latency,
                    gamemode = entry.GameMode,
                    listed = entry.Listed,
                    displayName = entry.DisplayName,
                    entityId = match?.Id,
                    x = match is null ? (double?)null : Math.Round(match.Position.X, 2),
                    y = match is null ? (double?)null : Math.Round(match.Position.Y, 2),
                    z = match is null ? (double?)null : Math.Round(match.Position.Z, 2),
                });
            }

            return McpResult.Ok(new
            {
                includeSelf,
                includeCoordinates,
                count = rows.Count,
                players = rows,
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_recent_events: high-signal runtime events after a cursor, newest last.</summary>
    public Task<McpResult> GetRecentEventsAsync(long afterId, int maxCount, string? typeFilter, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.SessionStatus);
        if (!gate.Success)
            return Task.FromResult(gate);

        try
        {
            (IReadOnlyList<McpEventEntry> entries, long latest) =
                _store.GetEvents(afterId, maxCount, typeFilter);
            return Task.FromResult(McpResult.Ok(new
            {
                afterId,
                latestId = latest,
                count = entries.Count,
                events = entries.Select(e => new
                {
                    id = e.Id,
                    timestampUtc = e.TimestampUtc,
                    type = e.Type,
                    data = e.Data,
                }).ToList(),
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(MapSessionException(ex));
        }
    }

    /// <summary>mcc_chat_history: recent chat/system lines seen by the client.</summary>
    public Task<McpResult> GetChatHistoryAsync(int maxCount, bool includeJson, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.SessionStatus);
        if (!gate.Success)
            return Task.FromResult(gate);

        try
        {
            (IReadOnlyList<McpChatEntry> entries, long latest) = _store.GetChat(maxCount);
            return Task.FromResult(McpResult.Ok(new
            {
                count = entries.Count,
                latestId = latest,
                jsonNote = includeJson
                    ? "Raw JSON components are not retained; text, sender and kind are."
                    : null,
                entries = entries.Select(e => new
                {
                    timestampUtc = e.TimestampUtc,
                    kind = e.Kind,
                    text = e.Text,
                    sender = e.Sender,
                    message = e.Message,
                }).ToList(),
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(MapSessionException(ex));
        }
    }

    /// <summary>
    /// mcc_loaded_bots reports the loaded plugins.
    /// The name is kept for catalog parity; the payload says what it is.
    /// </summary>
    public Task<McpResult> GetLoadedBotsAsync(CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.SessionStatus);
        if (!gate.Success)
            return Task.FromResult(gate);

        try
        {
            var host = _context.Client.PluginHost;
            if (host is null)
            {
                return Task.FromResult(McpResult.Ok(new
                {
                    count = 0,
                    bots = new List<object>(),
                    note = "No plugin host is attached.",
                }));
            }

            var rows = host.List()
                .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Select(p => new
                {
                    name = p.Id,
                    version = p.Version,
                    enabled = p.Enabled,
                    loaded = p.Loaded,
                    status = p.Status?.ToString(),
                })
                .ToList<object>();

            return Task.FromResult(McpResult.Ok(new
            {
                count = rows.Count,
                bots = rows,
                note = "MCC 2.0 reports loaded plugins; the legacy bot/script list has no counterpart.",
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(MapSessionException(ex));
        }
    }

    /// <summary>mcc_internal_commands_list: name, usage and description for every registered command.</summary>
    public Task<McpResult> GetInternalCommandsAsync(CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.ChatAndCommands);
        if (!gate.Success)
            return Task.FromResult(gate);

        try
        {
            IReadOnlyList<CommandSummary> commands = _context.Client.Commands.ListCommands();
            var rows = commands
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new { name = c.Name, usage = c.Usage, description = c.Description })
                .ToList<object>();
            return Task.FromResult(McpResult.Ok(new { count = rows.Count, commands = rows }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(MapSessionException(ex));
        }
    }

    /// <summary>mcc_agent_guidance: the canonical operator-prompt bundle for external agents.</summary>
    public Task<McpResult> GetAgentGuidanceAsync(CancellationToken ct)
        => Task.FromResult(McpResult.Ok(_guidance.GetToolPayload(BuildCapabilityStatus())));

    internal Dictionary<string, bool> BuildCapabilityStatus()
        => new()
        {
            ["sessionStatus"] = IsEnabled(McpCapability.SessionStatus),
            ["chatAndCommands"] = IsEnabled(McpCapability.ChatAndCommands),
            ["movement"] = IsEnabled(McpCapability.Movement),
            ["inventory"] = IsEnabled(McpCapability.Inventory),
            ["entityWorld"] = IsEnabled(McpCapability.EntityWorld),
        };

    private int CurrentProtocol()
    {
        try
        {
            // Read off the live session info when available; 0 means unknown/offline.
            SessionInfoSnapshot? info = _context.Game.Session
                .GetInfoAsync().GetAwaiter().GetResult();
            return info?.Protocol ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static IReadOnlyList<object> FilterCatalog(
        IEnumerable<(string Name, string Label)> rows, string? filter, int maxCount, out int total)
    {
        var all = rows.ToList();
        total = all.Count;
        IEnumerable<(string Name, string Label)> query = all;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            string needle = filter.Trim();
            query = query.Where(r =>
                r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || r.Label.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        return query
            .Take(Math.Clamp(maxCount, 1, 5000))
            .Select(r => (object)new { name = r.Name, typeLabel = r.Label })
            .ToList();
    }

    /// <summary>mcc_materials_list: item ids for the negotiated protocol (namespaced vocabulary, not Material enum names).</summary>
    public Task<McpResult> GetMaterialsListAsync(string? filter, int maxCount, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.EntityWorld);
        if (!gate.Success)
            return Task.FromResult(gate);

        try
        {
            int protocol = CurrentProtocol();
            if (protocol <= 0)
            {
                return Task.FromResult(McpResult.Fail(
                    McpErrorCodes.Disconnected, "No live session to resolve registries from."));
            }

            var access = JavaGameData.Registries(protocol);
            var rows = access.Items.Select(e => (e.Id.ToString(), e.Id.ToString()));
            IReadOnlyList<object> materials = FilterCatalog(rows, filter, maxCount, out int total);
            return Task.FromResult(McpResult.Ok(new
            {
                total,
                count = materials.Count,
                filter,
                vocabularyNote = "Namespaced item ids for the negotiated protocol; legacy Material enum names are not used.",
                materials,
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(MapSessionException(ex));
        }
    }

    /// <summary>mcc_block_types_list: block ids for the negotiated protocol.</summary>
    public Task<McpResult> GetBlockTypesListAsync(string? filter, int maxCount, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.EntityWorld);
        if (!gate.Success)
            return Task.FromResult(gate);

        try
        {
            int protocol = CurrentProtocol();
            if (protocol <= 0)
            {
                return Task.FromResult(McpResult.Fail(
                    McpErrorCodes.Disconnected, "No live session to resolve registries from."));
            }

            var access = JavaGameData.Registries(protocol);
            var rows = access.Blocks.Select(e => (e.Id.ToString(), e.Id.ToString()));
            IReadOnlyList<object> blockTypes = FilterCatalog(rows, filter, maxCount, out int total);
            return Task.FromResult(McpResult.Ok(new
            {
                total,
                count = blockTypes.Count,
                filter,
                blockTypes,
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(MapSessionException(ex));
        }
    }

    /// <summary>mcc_entity_types_list: entity-type ids for the negotiated protocol.</summary>
    public Task<McpResult> GetEntityTypesListAsync(string? filter, int maxCount, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.EntityWorld);
        if (!gate.Success)
            return Task.FromResult(gate);

        try
        {
            int protocol = CurrentProtocol();
            if (protocol <= 0)
            {
                return Task.FromResult(McpResult.Fail(
                    McpErrorCodes.Disconnected, "No live session to resolve registries from."));
            }

            var access = JavaGameData.Registries(protocol);
            var rows = access.EntityTypes.Select(e => (e.Id.ToString(), e.Id.ToString()));
            IReadOnlyList<object> entityTypes = FilterCatalog(rows, filter, maxCount, out int total);
            return Task.FromResult(McpResult.Ok(new
            {
                total,
                count = entityTypes.Count,
                filter,
                entityTypes,
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(MapSessionException(ex));
        }
    }

    /// <summary>mcc_world_block_at: block identity at a position.</summary>
    public async Task<McpResult> GetWorldBlockAtAsync(int x, int y, int z, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.EntityWorld);
        if (!gate.Success)
            return gate;

        try
        {
            var pos = new Umpk.Geometry.BlockPos(x, y, z);
            BlockInfo block = await _context.Game.World.GetBlockAsync(pos, ct).ConfigureAwait(false);
            return McpResult.Ok(new
            {
                x,
                y,
                z,
                chunkLoaded = block.ChunkLoaded,
                material = block.IsAir ? "air" : block.BlockId,
                blockId = block.BlockId,
                stateId = block.StateId,
                isAir = block.IsAir,
                isSolid = block.IsSolid,
                isFluid = block.IsFluid,
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_block_scan: cube scan around the player, skipping air.</summary>
    public async Task<McpResult> ScanNearbyBlocksAsync(int radius, int maxCount, string? materialFilter, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.EntityWorld);
        if (!gate.Success)
            return gate;

        radius = Math.Clamp(radius, 1, 12);
        maxCount = Math.Clamp(maxCount, 1, 2000);

        try
        {
            PlayerStatus player = await _context.Game.Player.GetStatusAsync(ct).ConfigureAwait(false);
            var origin = new Umpk.Geometry.BlockPos(
                (int)Math.Floor(player.Position.X),
                (int)Math.Floor(player.Position.Y) - 1,
                (int)Math.Floor(player.Position.Z));
            var hits = new List<object>();
            for (int dx = -radius; dx <= radius && hits.Count < maxCount; dx++)
            {
                for (int dy = -radius; dy <= radius && hits.Count < maxCount; dy++)
                {
                    for (int dz = -radius; dz <= radius && hits.Count < maxCount; dz++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var pos = new Umpk.Geometry.BlockPos(origin.X + dx, origin.Y + dy, origin.Z + dz);
                        BlockInfo block;
                        try
                        {
                            block = await _context.Game.World.GetBlockAsync(pos, ct).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        if (block.IsAir || !block.ChunkLoaded)
                            continue;

                        if (!string.IsNullOrWhiteSpace(materialFilter)
                            && !block.BlockId.Contains(materialFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        hits.Add(new
                        {
                            x = pos.X,
                            y = pos.Y,
                            z = pos.Z,
                            material = block.BlockId,
                            stateId = block.StateId,
                            distance = Math.Round(distance, 2),
                        });
                    }
                }
            }

            return McpResult.Ok(new
            {
                center = new { x = origin.X, y = origin.Y, z = origin.Z },
                radius,
                count = hits.Count,
                blocks = hits,
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_blocks_find: nearest blocks matching a name/id query, sorted by distance.</summary>
    public async Task<McpResult> FindBlocksAsync(string? query, int radius, int maxCount, bool exactMatch, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.EntityWorld);
        if (!gate.Success)
            return gate;

        radius = Math.Clamp(radius, 1, 32);
        maxCount = Math.Clamp(maxCount, 1, 5000);

        try
        {
            PlayerStatus player = await _context.Game.Player.GetStatusAsync(ct).ConfigureAwait(false);
            var origin = new Umpk.Geometry.BlockPos(
                (int)Math.Floor(player.Position.X),
                (int)Math.Floor(player.Position.Y),
                (int)Math.Floor(player.Position.Z));
            var scored = new List<(double Distance, object Row)>();

            if (!string.IsNullOrWhiteSpace(query))
            {
                IReadOnlyList<Umpk.Geometry.BlockPos> found = await _context.Game.World
                    .FindBlocksAsync(query.Trim(), radius, maxCount, ct).ConfigureAwait(false);
                foreach (Umpk.Geometry.BlockPos pos in found)
                {
                    ct.ThrowIfCancellationRequested();
                    BlockInfo block;
                    try
                    {
                        block = await _context.Game.World.GetBlockAsync(pos, ct).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (exactMatch && !string.Equals(block.BlockId, query.Trim(), StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(block.BlockId.Split(':').Last(), query.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;

                    double dx = pos.X - origin.X;
                    double dy = pos.Y - origin.Y;
                    double dz = pos.Z - origin.Z;
                    double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    scored.Add((distance, new
                    {
                        x = pos.X,
                        y = pos.Y,
                        z = pos.Z,
                        material = block.BlockId,
                        stateId = block.StateId,
                        distance = Math.Round(distance, 2),
                    }));
                }
            }

            var ordered = scored.OrderBy(s => s.Distance).Take(maxCount).Select(s => s.Row).ToList();
            return McpResult.Ok(new
            {
                query,
                radius,
                count = ordered.Count,
                blocks = ordered,
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }
}
