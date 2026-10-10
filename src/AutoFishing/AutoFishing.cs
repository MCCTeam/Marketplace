// AutoFishing: casts a rod, watches the bobber for a bite (motion, velocity or the splash sound), reels in and casts again.
// It can also walk a list of positions between casts and swap in a fresh rod.
//
// Two gaps in this core, and the workarounds: a bobber carries no owner id, so the one that appears near the player while we wait for our own cast is taken as ours; and item-entity contents are not decoded, so a catch is named by diffing the player inventory.

using System.Text;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client.Actions;
using Umpk.Client.Events;
using Umpk.Commands;
using Umpk.Game.Entities;
using Umpk.Game.Inventory;
using Umpk.Game.Items;
using Umpk.Geometry;
using Umpk.Pathfinding.Goals;

namespace DMCBK.OfficialPlugins.AutoFishing;

/// <summary>Bite detection, the movement cycler and the inventory diff. Pure logic, unit tested.</summary>
public static class FishingRules
{
    /// <summary>The registry path of the fishing bobber entity.</summary>
    public const string BobberPath = "fishing_bobber";

    /// <summary>The vanilla fishing rod durability, the denominator Durability_Limit is read against.</summary>
    public const int RodMaxDamage = 64;

    /// <summary>True when an entity type id is the fishing bobber (both the modern and 1.8 spellings).</summary>
    public static bool IsBobber(string? typeId)
    {
        string bare = BarePath(typeId);
        return string.Equals(bare, BobberPath, StringComparison.Ordinal)
            || string.Equals(bare, "fishing_hook", StringComparison.Ordinal)
            || string.Equals(bare, "bobber", StringComparison.Ordinal);
    }

    /// <summary>Strips a namespace and lowercases an id.</summary>
    public static string BarePath(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return string.Empty;

        string value = id.Trim();
        int colon = value.IndexOf(':');
        return (colon >= 0 ? value[(colon + 1)..] : value).ToLowerInvariant();
    }

    /// <summary>Whether a sound name is the fishing-bobber splash.</summary>
    /// <remarks>
    /// The namespace is optional so both wire spellings match: id-addressed packets resolve through the registry to <c>minecraft:entity.fishing_bobber.splash</c>, while older name-addressed packets carry the bare path.
    /// </remarks>
    public static bool IsSplashSound(string? soundName)
    {
        if (string.IsNullOrEmpty(soundName))
            return false;

        ReadOnlySpan<char> name = soundName;
        int colon = name.IndexOf(':');
        if (colon >= 0)
            name = name[(colon + 1)..];

        return name.Equals("entity.fishing_bobber.splash", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A bite by motion: the bobber barely moves horizontally while it drops or jumps.
    /// Both thresholds are read as absolute values.
    /// </summary>
    public static bool IsBiteByMotion(double dx, double dy, double dz, double stationaryThreshold, double hookThreshold)
        => Math.Abs(dx) < Math.Abs(stationaryThreshold)
           && Math.Abs(dz) < Math.Abs(stationaryThreshold)
           && Math.Abs(dy) > Math.Abs(hookThreshold);

    /// <summary>A bite by velocity: the bobber is yanked down past a negative Y threshold.</summary>
    public static bool IsBiteByVelocity(double velocityY, double threshold) => velocityY <= threshold;

    /// <summary>The cast backoff: double the timeout, capped at 6000 ticks.</summary>
    public static int NextCastTimeout(int current) => current < 6000 ? current * 2 : current;

    /// <summary>Converts seconds to whole client ticks at 20 TPS.</summary>
    public static int SecondsToTicks(double seconds) => (int)Math.Round(seconds * 20.0);

    /// <summary>
    /// Clamps the movement cursor before use.
    /// The walk over the Movements list ping-pongs: run off the end and it turns around one entry short of the edge.
    /// </summary>
    public static (int Index, int Direction) ClampCursor(int index, int direction, int count)
    {
        if (count <= 0)
            return (0, direction);

        if (index >= count)
            return (Math.Max(0, count - 2), -1);

        if (index < 0)
            return (Math.Min(count - 1, 1), 1);

        return (index, direction);
    }

    /// <summary>The rod durability left, which is what Durability_Limit is compared against.</summary>
    public static int RodDurability(int damage, int maxDamage)
        => (maxDamage > 0 ? maxDamage : RodMaxDamage) - damage;

    /// <summary>Counts every item in a slot list, keyed by registry id.</summary>
    public static Dictionary<string, int> CountItems(IReadOnlyList<(string ItemId, int Count)> slots)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((string itemId, int count) in slots)
        {
            if (string.IsNullOrEmpty(itemId) || count <= 0)
                continue;

            counts[itemId] = counts.TryGetValue(itemId, out int existing) ? existing + count : count;
        }

        return counts;
    }

    /// <summary>
    /// The items gained between two inventory counts: the positive part of after minus before.
    /// A catch is named this way because item-entity contents are not decoded.
    /// </summary>
    public static Dictionary<string, int> Gained(
        IReadOnlyDictionary<string, int> before, IReadOnlyDictionary<string, int> after)
    {
        var gained = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, int> entry in after)
        {
            int previous = before.TryGetValue(entry.Key, out int value) ? value : 0;
            int delta = entry.Value - previous;
            if (delta > 0)
                gained[entry.Key] = delta;
        }

        return gained;
    }
}

/// <summary>An optional position in a Movements entry.</summary>
public sealed class MovementXyz
{
    public double x { get; set; }

    public double y { get; set; }

    public double z { get; set; }
}

/// <summary>An optional facing in a Movements entry.</summary>
public sealed class MovementFacing
{
    /// <summary>The yaw in degrees.</summary>
    public double yaw { get; set; }

    /// <summary>The pitch in degrees.</summary>
    public double pitch { get; set; }
}

/// <summary>One entry of the Movements list: an optional position and an optional facing.</summary>
public sealed class MovementConfig
{
    /// <summary>Where to stand, or null to stay put.</summary>
    public MovementXyz? XYZ { get; set; }

    /// <summary>Where to look, or null to keep the current facing.</summary>
    public MovementFacing? facing { get; set; }
}

/// <summary>The user-editable, persisted settings for AutoFishing (serialized to settings.toml).</summary>
public sealed class AutoFishingSettings : IValidatablePluginSettings
{
    /// <summary>Master switch.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; }

    /// <summary>Recast when the bobber despawns instead of stopping.</summary>
    [TomlInlineComment("$settings.antidespawn$")]
    public bool Antidespawn { get; set; }

    /// <summary>Cast with the main hand (false casts with the off hand).</summary>
    [TomlInlineComment("$settings.mainhand$")]
    public bool Mainhand { get; set; } = true;

    /// <summary>Start fishing automatically after joining or respawning.</summary>
    [TomlInlineComment("$settings.auto_start$")]
    public bool Auto_Start { get; set; } = true;

    /// <summary>Seconds between the durability check and the cast.</summary>
    [TomlInlineComment("$settings.cast_delay$")]
    public double Cast_Delay { get; set; } = 0.4;

    /// <summary>Seconds to wait after joining before the first cast.</summary>
    [TomlInlineComment("$settings.fishing_delay$")]
    public double Fishing_Delay { get; set; } = 3.0;

    /// <summary>Seconds without a bite before recasting.</summary>
    [TomlInlineComment("$settings.fishing_timeout$")]
    public double Fishing_Timeout { get; set; } = 300.0;

    /// <summary>Minimum remaining rod durability (out of 64).</summary>
    [TomlInlineComment("$settings.durability_limit$")]
    public double Durability_Limit { get; set; } = 2;

    /// <summary>Swap in a fresh rod from the inventory when the held one is worn out.</summary>
    [TomlInlineComment("$settings.auto_rod_switch$")]
    public bool Auto_Rod_Switch { get; set; } = true;

    /// <summary>Horizontal movement below this counts as stationary.</summary>
    [TomlInlineComment("$settings.stationary_threshold$")]
    public double Stationary_Threshold { get; set; } = 0.001;

    /// <summary>Vertical movement above this while stationary is a bite.</summary>
    [TomlInlineComment("$settings.hook_threshold$")]
    public double Hook_Threshold { get; set; } = 0.2;

    /// <summary>Also detect the bite from the bobber velocity.</summary>
    [TomlInlineComment("$settings.enable_velocity_detection$")]
    public bool Enable_Velocity_Detection { get; set; } = true;

    /// <summary>Bobber Y velocity at or below this is a bite (negative).</summary>
    [TomlInlineComment("$settings.velocity_hook_threshold$")]
    public double Velocity_Hook_Threshold { get; set; } = -0.2;

    /// <summary>Detect the bite from the bobber splash sound as well as from motion.</summary>
    [TomlInlineComment("$settings.enable_sound_detection$")]
    public bool Enable_Sound_Detection { get; set; } = true;

    /// <summary>How near the splash has to be, in blocks, to count as this bot's bite.</summary>
    [TomlInlineComment("$settings.sound_distance$")]
    public double Sound_Distance { get; set; } = 5.0;

    /// <summary>Seconds after the bobber spawns before bite detection arms.</summary>
    [TomlInlineComment("$settings.detection_warmup$")]
    public double Detection_Warmup { get; set; } = 1.0;

    /// <summary>Trace every bobber spawn, move and despawn.</summary>
    [TomlInlineComment("$settings.log_fish_bobber$")]
    public bool Log_Fish_Bobber { get; set; }

    /// <summary>Walk the Movements list between casts.</summary>
    [TomlInlineComment("$settings.enable_move$")]
    public bool Enable_Move { get; set; }

    /// <summary>The positions and facings to cycle through.</summary>
    [TomlPrecedingComment("$settings.movements$")]
    public MovementConfig[] Movements { get; set; } =
    [
        new MovementConfig { facing = new MovementFacing { yaw = 12.34, pitch = -23.45 } },
    ];

    /// <summary>Clamps and normalizes on load or reload.</summary>
    public void Validate()
    {
        Movements ??= [];

        if (Cast_Delay < 0)
            Cast_Delay = 0;

        if (Fishing_Delay < 0)
            Fishing_Delay = 0;

        if (Fishing_Timeout < 0)
            Fishing_Timeout = 0;

        if (Durability_Limit < 0)
            Durability_Limit = 0;
        else if (Durability_Limit > 64)
            Durability_Limit = 64;

        if (Stationary_Threshold < 0)
            Stationary_Threshold = -Stationary_Threshold;

        if (Hook_Threshold < 0)
            Hook_Threshold = -Hook_Threshold;

        if (Velocity_Hook_Threshold > 0)
            Velocity_Hook_Threshold = -Velocity_Hook_Threshold;

        if (Sound_Distance < 0)
            Sound_Distance = -Sound_Distance;

        if (Detection_Warmup < 0)
            Detection_Warmup = 0;
    }
}

/// <summary>The AutoFishing plugin entry point.</summary>
public sealed class AutoFishingPlugin : IPlugin
{
    private enum FishingState
    {
        WaitJoinGame,
        WaitingToCast,
        CastingRod,
        WaitingFishingBobber,
        WaitingFishToBite,
        StartMove,
        WaitingMovement,
        DurabilityCheck,
        Stopping,
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _caught = new(StringComparer.Ordinal);

    private AutoFishingSettings _settings = new();
    private ILogger? _logger;
    private IPluginLocalization? _strings;
    private ISessionScope? _session;

    private FishingState _state = FishingState.WaitJoinGame;
    private int _counter;
    private int _castTimeout = 12;
    private int _fishCount;
    private bool _isFishing;
    private bool _waitingRod;
    private bool _moving;
    private bool _warnedSound;
    private int _bobberId = -1;
    private Vec3d _lastBobberPosition;
    private double _lastVelocityY;
    private bool _hasVelocitySample;
    private DateTime _bobberSpawnTime = DateTime.MinValue;
    private DateTime _caughtTime = DateTime.MinValue;
    private Dictionary<string, int> _inventoryBefore = new(StringComparer.Ordinal);
    private int _cursor;
    private int _direction = 1;
    private float _nextYaw;
    private float _nextPitch;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "auto-fishing";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<AutoFishingSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        _settings = context.Settings.Load<AutoFishingSettings>();
        _logger = context.Logger;
        _strings = context.Strings;

        context.Commands.Register(new AutoFishingCommand(this, context.Strings));

        context.SessionStarted += (_, e) => OnSessionStarted(e.Session);
        context.SessionEnded += (_, _) =>
        {
            StopFishing();
            lock (_gate)
            {
                _session = null;
                _bobberId = -1;
                _bobberSpawnTime = DateTime.MinValue;
            }
        };

        return Task.CompletedTask;
    }

    /// <summary>Arms the state machine.</summary>
    internal void StartFishing()
    {
        lock (_gate)
        {
            _isFishing = false;
            _counter = 0;
            _state = FishingState.StartMove;
        }
    }

    /// <summary>Disarms the state machine.</summary>
    internal void StopFishing()
    {
        lock (_gate)
        {
            _isFishing = false;
            _state = FishingState.Stopping;
        }
    }

    /// <summary>Reels the line in, if one is out, and stops.</summary>
    internal void StopAndReel()
    {
        ISessionScope? session;
        bool reel;
        lock (_gate)
        {
            session = _session;
            reel = _state == FishingState.WaitingFishToBite;
        }

        if (reel && session is not null)
            session.Scheduler.Post(() => _ = UseRodAsync(session));

        StopFishing();
    }

    /// <summary>The catch counters, for the status command.</summary>
    internal KeyValuePair<string, int>[] Caught
    {
        get
        {
            lock (_gate)
            {
                var entries = new KeyValuePair<string, int>[_caught.Count];
                ((ICollection<KeyValuePair<string, int>>)_caught).CopyTo(entries, 0);
                Array.Sort(entries, (a, b) => a.Value.CompareTo(b.Value));
                return entries;
            }
        }
    }

    /// <summary>Clears the catch counters.</summary>
    internal void ClearCaught()
    {
        lock (_gate)
            _caught.Clear();
    }

    private void OnSessionStarted(ISessionScope session)
    {
        lock (_gate)
        {
            _session = session;
            _state = FishingState.WaitJoinGame;
            _counter = 0;
            _isFishing = false;
            _bobberId = -1;
            _castTimeout = 12;
        }

        if (!session.State.Features.Entities)
        {
            _logger?.LogWarning("{Message}", _strings?.Get("entities_required"));
            return;
        }

        if (!session.State.Features.Inventory)
            _logger?.LogWarning("{Message}", _strings?.Get("no_inventory_handling"));

        if (_settings.Enable_Sound_Detection)
            session.Events.Subscribe<SoundPlayed>(e => OnSoundPlayed(session, e));

        session.Events.Subscribe<EntitySpawned>(e => OnEntitySpawned(session, e.EntityId));
        session.Events.Subscribe<EntityRemoved>(e => OnEntityRemoved(e.EntityId));
        session.Events.Subscribe<EntityMoved>(e => OnEntityMoved(session, e.EntityId));
        session.Events.Subscribe<Respawned>(_ => AutoStart());
        session.Events.Subscribe<Died>(_ => StopFishing());
        session.Scheduler.OnTick(() => OnTick(session));
        AutoStart();
    }

    private void AutoStart()
    {
        if (!_settings.Enabled)
            return;

        if (_settings.Auto_Start)
        {
            _logger?.LogInformation("{Message}", _strings?.Format("start_at", _settings.Fishing_Delay));
            lock (_gate)
            {
                _isFishing = false;
                _counter = FishingRules.SecondsToTicks(_settings.Fishing_Delay);
                _state = FishingState.StartMove;
            }
        }
        else
        {
            lock (_gate)
                _state = FishingState.WaitJoinGame;
        }
    }

    private void OnEntitySpawned(ISessionScope session, int entityId)
    {
        if (!session.State.Entities.TryGet(entityId, out Entity? entity)
            || !FishingRules.IsBobber(entity.Type.Id.ToString()))
            return;

        lock (_gate)
        {
            // No owner id on the wire, so a bobber that shows up near the player while we wait for our own cast is treated as ours.
            if (_state != FishingState.WaitingFishingBobber && _state != FishingState.CastingRod)
                return;

            if (entity.Position.Subtract(session.State.Self.Position).Length() > 40.0)
                return;

            if (_settings.Log_Fish_Bobber)
            {
                _logger?.LogInformation(
                    "{Message}",
                    _strings?.Format(
                        "bobber_spawned", entityId, entity.Position.X, entity.Position.Y, entity.Position.Z));
            }

            _bobberId = entityId;
            _lastBobberPosition = entity.Position;
            _hasVelocitySample = false;
            _isFishing = true;
            _bobberSpawnTime = DateTime.UtcNow;
            _castTimeout = 24;
            _counter = 0;
            _state = FishingState.WaitingFishToBite;
        }

        _logger?.LogInformation("{Message}", _strings?.Get("bobber_in_water"));
        _ = SnapshotInventoryAsync(session);
    }

    private async Task SnapshotInventoryAsync(ISessionScope session)
    {
        try
        {
            Dictionary<string, int> counts = await session.Client
                .InvokeAsync(c => CountInventory(c.State.Features.Inventory ? c.State.Inventory.PlayerSlots : []),
                    session.Detached).ConfigureAwait(false);
            lock (_gate)
                _inventoryBefore = counts;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("snapshot_failed"));
        }
    }

    private static Dictionary<string, int> CountInventory(IReadOnlyList<ItemStack> slots)
    {
        var pairs = new List<(string ItemId, int Count)>(slots.Count);
        foreach (ItemStack stack in slots)
        {
            if (!stack.IsEmpty)
                pairs.Add((stack.Item.Id.ToString(), stack.Count));
        }

        return FishingRules.CountItems(pairs);
    }

    private void OnEntityRemoved(int entityId)
    {
        bool recast = false;
        lock (_gate)
        {
            if (entityId != _bobberId)
                return;

            if (_settings.Log_Fish_Bobber)
                _logger?.LogInformation("{Message}", _strings?.Format("bobber_despawned", entityId));

            _bobberId = -1;
            if (_isFishing)
            {
                _isFishing = false;
                if (_settings.Antidespawn)
                {
                    _counter = FishingRules.SecondsToTicks(_settings.Cast_Delay);
                    _state = FishingState.WaitingToCast;
                    recast = true;
                }
            }
        }

        if (recast)
            _logger?.LogInformation("{Message}", _strings?.Get("despawned"));
    }

    private void OnEntityMoved(ISessionScope session, int entityId)
    {
        bool bite = false;
        lock (_gate)
        {
            if (!_isFishing || entityId != _bobberId || _state != FishingState.WaitingFishToBite)
                return;

            if (!session.State.Entities.TryGet(entityId, out Entity? bobber))
                return;

            Vec3d position = bobber.Position;
            double dx = _lastBobberPosition.X - position.X;
            double dy = _lastBobberPosition.Y - position.Y;
            double dz = _lastBobberPosition.Z - position.Z;
            _lastBobberPosition = position;

            if (_settings.Log_Fish_Bobber)
                _logger?.LogInformation("{Message}", _strings?.Format("bobber_delta", dx, dy, dz));

            if (FishingRules.IsBiteByMotion(dx, dy, dz, _settings.Stationary_Threshold, _settings.Hook_Threshold))
                bite = TryLatchBite();
        }

        if (bite)
            OnCaughtFish(session);
    }

    /// <summary>Detects a bite from the bobber's splash sound, alongside motion detection.</summary>
    /// <remarks>
    /// A server that names no sound (pre-1.14 protocols carry no sound registry, and a resource-pack sound arrives inline) leaves SoundName null, so the bot keeps using motion and warns once rather than leaving the setting silently inert.
    /// The distance gate stops another player's bobber nearby from counting as our catch.
    /// </remarks>
    private void OnSoundPlayed(ISessionScope session, SoundPlayed sound)
    {
        if (sound.SoundName is not { Length: > 0 } name)
        {
            if (!_warnedSound)
            {
                _warnedSound = true;
                _logger?.LogWarning("{Message}", _strings?.Get("sound_detection_unavailable"));
            }

            return;
        }

        if (!FishingRules.IsSplashSound(name))
            return;

        bool bite = false;
        lock (_gate)
        {
            if (!_isFishing || _bobberId < 0 || _state != FishingState.WaitingFishToBite)
                return;

            if (!session.State.Entities.TryGet(_bobberId, out Entity? bobber))
                return;

            double dx = bobber.Position.X - sound.Position.X;
            double dy = bobber.Position.Y - sound.Position.Y;
            double dz = bobber.Position.Z - sound.Position.Z;
            double limit = _settings.Sound_Distance;
            if ((dx * dx) + (dy * dy) + (dz * dz) > limit * limit)
                return;

            bite = TryLatchBite();
        }

        if (bite)
            OnCaughtFish(session);
    }

    // Must be called under _gate.
    // Applies the warmup and the one-second re-catch guard.
    private bool TryLatchBite()
    {
        if (!_isFishing || _bobberId < 0 || _state != FishingState.WaitingFishToBite)
            return false;

        if ((DateTime.UtcNow - _bobberSpawnTime).TotalSeconds < _settings.Detection_Warmup)
            return false;

        if ((DateTime.UtcNow - _caughtTime).TotalSeconds <= 1)
            return false;

        _isFishing = false;
        _caughtTime = DateTime.UtcNow;
        return true;
    }

    private void OnTick(ISessionScope session)
    {
        if (!_settings.Enabled)
            return;

        bool cast = false;
        bool durabilityCheck = false;
        bool startMove = false;
        bool velocityBite = false;

        lock (_gate)
        {
            switch (_state)
            {
                case FishingState.WaitJoinGame:
                case FishingState.Stopping:
                    break;

                case FishingState.WaitingToCast:
                    if (--_counter < 0)
                    {
                        _state = FishingState.CastingRod;
                        cast = true;
                    }

                    break;

                case FishingState.CastingRod:
                    // The cast itself is issued from WaitingToCast; this state only waits for the bobber.
                    _counter = 0;
                    _state = FishingState.WaitingFishingBobber;
                    break;

                case FishingState.WaitingFishingBobber:
                    if (++_counter > _castTimeout)
                    {
                        _castTimeout = FishingRules.NextCastTimeout(_castTimeout);
                        _logger?.LogInformation("{Message}", _strings?.Format("cast_timeout", _castTimeout / 20.0));
                        _counter = FishingRules.SecondsToTicks(_settings.Cast_Delay);
                        _state = FishingState.WaitingToCast;
                    }

                    break;

                case FishingState.WaitingFishToBite:
                    // No event carries entity velocity, so it is polled here.
                    // Only a value that actually changed counts: a bobber sitting on a stale negative Y (one that landed on a block, say) would otherwise report a bite on every tick.
                    if (_settings.Enable_Velocity_Detection
                        && _bobberId >= 0
                        && session.State.Entities.TryGet(_bobberId, out Entity? bobber))
                    {
                        double velocityY = bobber.Velocity.Y;
                        bool changed = !_hasVelocitySample || Math.Abs(velocityY - _lastVelocityY) > 1e-9;
                        _lastVelocityY = velocityY;
                        _hasVelocitySample = true;

                        if (changed && FishingRules.IsBiteByVelocity(velocityY, _settings.Velocity_Hook_Threshold))
                            velocityBite = TryLatchBite();
                    }

                    if (!velocityBite && ++_counter > FishingRules.SecondsToTicks(_settings.Fishing_Timeout))
                    {
                        _logger?.LogInformation("{Message}", _strings?.Get("fishing_timeout"));
                        _counter = FishingRules.SecondsToTicks(_settings.Cast_Delay);
                        _state = FishingState.WaitingToCast;
                    }

                    break;

                case FishingState.StartMove:
                    if (--_counter < 0)
                    {
                        if (_settings.Enable_Move && _settings.Movements.Length > 0 && session.State.Features.Terrain)
                        {
                            _state = FishingState.WaitingMovement;
                            startMove = true;
                        }
                        else
                        {
                            if (_settings.Enable_Move && !session.State.Features.Terrain)
                                _logger?.LogWarning("{Message}", _strings?.Get("terrain_required"));

                            _counter = FishingRules.SecondsToTicks(_settings.Cast_Delay);
                            _state = FishingState.DurabilityCheck;
                            durabilityCheck = true;
                        }
                    }

                    break;

                case FishingState.WaitingMovement:
                    if (!_moving)
                    {
                        _state = FishingState.DurabilityCheck;
                        durabilityCheck = true;
                    }

                    break;

                case FishingState.DurabilityCheck:
                    durabilityCheck = true;
                    break;
            }
        }

        if (velocityBite)
        {
            OnCaughtFish(session);
            return;
        }

        if (startMove)
        {
            _ = UpdateLocationAsync(session);
            return;
        }

        if (cast)
        {
            _ = UseRodAsync(session);
            return;
        }

        if (durabilityCheck)
            _ = DurabilityCheckAsync(session);
    }

    private async Task UseRodAsync(ISessionScope session)
    {
        try
        {
            await session.Actions.Interaction
                .UseItemAsync(_settings.Mainhand ? Hand.Main : Hand.Off, session.Detached).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("cast_failed"));
        }
    }

    private void OnCaughtFish(ISessionScope session)
    {
        int count;
        Vec3d position;
        lock (_gate)
        {
            count = ++_fishCount;
            position = _lastBobberPosition;
            _counter = 0;
            _state = FishingState.StartMove;
        }

        _logger?.LogInformation(
            "{Message}",
            _settings.Enable_Move && _settings.Movements.Length > 0
                ? _strings?.Format("caught_at", position.X, position.Y, position.Z, count)
                : _strings?.Format("caught", count));

        _ = UseRodAsync(session);
        _ = ReportCatchAsync(session);
    }

    private async Task ReportCatchAsync(ISessionScope session)
    {
        try
        {
            // Give the server a moment to hand us the loot, then name it by diffing the inventory.
            await Task.Delay(TimeSpan.FromMilliseconds(1200), session.Detached).ConfigureAwait(false);
            Dictionary<string, int> after = await session.Client
                .InvokeAsync(c => CountInventory(c.State.Features.Inventory ? c.State.Inventory.PlayerSlots : []),
                    session.Detached).ConfigureAwait(false);

            Dictionary<string, int> before;
            lock (_gate)
            {
                before = _inventoryBefore;
                _inventoryBefore = after;
            }

            foreach (KeyValuePair<string, int> entry in FishingRules.Gained(before, after))
            {
                lock (_gate)
                {
                    _caught[entry.Key] = _caught.TryGetValue(entry.Key, out int existing)
                        ? existing + entry.Value
                        : entry.Value;
                }

                _logger?.LogInformation("{Message}", _strings?.Format("got", entry.Value, entry.Key));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("catch_name_failed"));
        }
    }

    private async Task UpdateLocationAsync(ISessionScope session)
    {
        _moving = true;
        try
        {
            MovementConfig current;
            lock (_gate)
            {
                (_cursor, _direction) = FishingRules.ClampCursor(_cursor, _direction, _settings.Movements.Length);
                current = _settings.Movements[_cursor];
                _cursor += _direction;
            }

            if (current.facing is { } facing)
            {
                _nextYaw = (float)facing.yaw;
                _nextPitch = (float)facing.pitch;
            }
            else
            {
                (_nextYaw, _nextPitch) = await session.Client
                    .InvokeAsync(c => (c.State.Self.Yaw, c.State.Self.Pitch), session.Detached).ConfigureAwait(false);
            }

            if (current.XYZ is { } xyz)
            {
                var goal = BlockPos.Containing(xyz.x, xyz.y, xyz.z);
                bool pathfinding = await session.Client
                    .InvokeAsync(c => c.State.Features.Pathfinding, session.Detached).ConfigureAwait(false);
                if (pathfinding)
                {
                    await session.Actions.Movement
                        .NavigateAsync(new GoalNear(goal.X, goal.Y, goal.Z, 1), session.Detached).ConfigureAwait(false);
                }
                else
                {
                    await session.Actions.Movement
                        .MoveToAsync(new Vec3d(xyz.x, xyz.y, xyz.z), session.Detached).ConfigureAwait(false);
                }
            }

            await session.Actions.Movement.SetRotationAsync(_nextYaw, _nextPitch, session.Detached).ConfigureAwait(false);
            _logger?.LogInformation("{Message}", _strings?.Format("looking_at", _nextYaw, _nextPitch));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("move_failed"));
        }
        finally
        {
            _moving = false;
        }
    }

    private async Task DurabilityCheckAsync(ISessionScope session)
    {
        bool ready;
        try
        {
            ready = await CheckRodAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("durability_failed"));
            ready = false;
        }

        lock (_gate)
        {
            if (_state != FishingState.DurabilityCheck)
                return;

            if (ready)
            {
                _counter = FishingRules.SecondsToTicks(_settings.Cast_Delay);
                _state = FishingState.WaitingToCast;
            }
        }
    }

    private async Task<bool> CheckRodAsync(ISessionScope session)
    {
        if (!session.State.Features.Inventory)
            return true;

        IReadOnlyList<ItemStack> slots = session.State.Inventory.PlayerSlots;
        int handSlot = _settings.Mainhand ? 36 + session.State.Self.HeldSlot : 45;
        ItemStack held = handSlot >= 0 && handSlot < slots.Count ? slots[handSlot] : ItemStack.Empty;

        if (!held.IsEmpty
            && string.Equals(FishingRules.BarePath(held.Item.Id.ToString()), "fishing_rod", StringComparison.Ordinal)
            && FishingRules.RodDurability(held.Damage, held.MaxDamage) >= _settings.Durability_Limit)
        {
            _waitingRod = false;
            return true;
        }

        if (!_waitingRod)
            _logger?.LogInformation("{Message}", _strings?.Get("no_rod"));

        if (_settings.Auto_Rod_Switch)
        {
            for (int slot = 0; slot < slots.Count; slot++)
            {
                if (slot == handSlot || slots[slot].IsEmpty)
                    continue;

                ItemStack candidate = slots[slot];
                int durability = FishingRules.RodDurability(candidate.Damage, candidate.MaxDamage);
                if (!string.Equals(FishingRules.BarePath(candidate.Item.Id.ToString()), "fishing_rod", StringComparison.Ordinal)
                    || durability < _settings.Durability_Limit)
                    continue;

                if (_settings.Mainhand)
                {
                    await session.Actions.Inventory
                        .ClickAsync(new ClickAction.Swap(slot, handSlot - 36), session.Detached).ConfigureAwait(false);
                }
                else
                {
                    // The off hand has no hotbar button, so swap it with three clicks instead.
                    await session.Actions.Inventory
                        .ClickAsync(new ClickAction.Pickup(slot, MouseButton.Left), session.Detached).ConfigureAwait(false);
                    await session.Actions.Inventory
                        .ClickAsync(new ClickAction.Pickup(handSlot, MouseButton.Left), session.Detached).ConfigureAwait(false);
                    await session.Actions.Inventory
                        .ClickAsync(new ClickAction.Pickup(slot, MouseButton.Left), session.Detached).ConfigureAwait(false);
                }

                _logger?.LogInformation("{Message}", _strings?.Format("switched", slot, durability));
                _waitingRod = false;
                return true;
            }
        }

        _waitingRod = true;
        return false;
    }
}

/// <summary>The <c>autofishing</c> console command.</summary>
internal sealed class AutoFishingCommand : CommandBase
{
    private readonly AutoFishingPlugin _plugin;
    private readonly IPluginLocalization _strings;

    internal AutoFishingCommand(AutoFishingPlugin plugin, IPluginLocalization strings)
    {
        _plugin = plugin;
        _strings = strings;
    }

    /// <inheritdoc/>
    public override string CmdName => "autofishing";

    /// <inheritdoc/>
    public override string CmdDesc => _strings.Get("command_desc");

    /// <inheritdoc/>
    public override string CmdUsage => _strings.Get("command_usage");

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Plugins;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("start", _strings.Get("usage_start")),
        new("stop", _strings.Get("usage_stop")),
        new("status", _strings.Get("usage_status")),
        new("status clear", _strings.Get("usage_status_clear")),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["autofishing start", "autofishing status"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["useitem", "inventory"];

    /// <inheritdoc/>
    public override string? ManTopic => "auto-fishing";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => ShowUsage(ctx.Source))
            .ThenLiteral("start", a => a.Executes(ctx => Start(ctx.Source)))
            .ThenLiteral("stop", a => a.Executes(ctx => Stop(ctx.Source)))
            .ThenLiteral("status", a => a
                .Executes(ctx => Status(ctx.Source))
                .ThenLiteral("clear", b => b.Executes(ctx => Clear(ctx.Source))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Start(CommandContext ctx)
    {
        _plugin.StartFishing();
        return ctx.Result.Ok(_strings.Get("started"));
    }

    private int Stop(CommandContext ctx)
    {
        _plugin.StopAndReel();
        return ctx.Result.Ok(_strings.Get("stopped"));
    }

    private int Status(CommandContext ctx)
    {
        KeyValuePair<string, int>[] caught = _plugin.Caught;
        if (caught.Length == 0)
            return ctx.Result.Ok(_strings.Get("status_empty"));

        var builder = new StringBuilder(_strings.Get("status_info"));
        foreach (KeyValuePair<string, int> entry in caught)
            builder.Append(Environment.NewLine).Append(_strings.Format("status_entry", entry.Value, entry.Key));

        return ctx.Result.Ok(builder.ToString());
    }

    private int Clear(CommandContext ctx)
    {
        _plugin.ClearCaught();
        return ctx.Result.Ok(_strings.Get("status_clear"));
    }
}
