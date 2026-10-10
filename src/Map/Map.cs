// Map - caches every in-game map item the server sends and renders one to the console and/or to an image file in this plugin's data/ folder, on demand (`maps render <id>`) or on every update.

using System.Globalization;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client.Events;
using Umpk.Commands;
using Umpk.Game.Players;

namespace DMCBK.OfficialPlugins.Map;

/// <summary>The persisted settings for Map.</summary>
public sealed class MapSettings : IValidatablePluginSettings
{
    /// <summary>Master switch for the plugin.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; } = true;

    /// <summary>Whether a render is printed to the console.</summary>
    [TomlInlineComment("$settings.render_in_console$")]
    public bool Render_In_Console { get; set; } = true;

    /// <summary>Whether a render is written to an image file in the plugin data folder.</summary>
    [TomlInlineComment("$settings.save_to_file$")]
    public bool Save_To_File { get; set; }

    /// <summary>Whether every map update renders immediately instead of only on command.</summary>
    [TomlInlineComment("$settings.auto_render_on_update$")]
    public bool Auto_Render_On_Update { get; set; }

    /// <summary>Whether previously rendered files are cleared when the plugin starts and stops.</summary>
    [TomlInlineComment("$settings.delete_all_on_unload$")]
    public bool Delete_All_On_Unload { get; set; } = true;

    /// <summary>Whether the first update of a map id is announced.</summary>
    [TomlInlineComment("$settings.notify_on_first_update$")]
    public bool Notify_On_First_Update { get; set; } = true;

    /// <summary>Whether the saved image is resized. The name is misspelled on purpose, so old configs load.</summary>
    [TomlInlineComment("$settings.rasize_rendered_image$")]
    public bool Rasize_Rendered_Image { get; set; }

    /// <summary>The square edge length the saved image is resized to.</summary>
    [TomlInlineComment("$settings.resize_to$")]
    public int Resize_To { get; set; } = 512;

    /// <summary>Whether a rendered map is handed to the DiscordBridge plugin.</summary>
    [TomlPrecedingComment("$settings.send_rendered$")]
    public bool Send_Rendered_To_Discord { get; set; }

    /// <summary>
    /// Where the Discord render goes: a comma-separated list of route:name and dm:owner-id entries.
    /// Empty means every channel that takes game chat.
    /// Examples: "route:general", "route:general,route:alerts", "dm:123456789", "route:general,dm:123456789".
    /// </summary>
    [TomlInlineComment("$settings.discord_target$")]
    public string Discord_Target { get; set; } = string.Empty;

    /// <summary>Whether a rendered map is handed to the TelegramBridge plugin.</summary>
    public bool Send_Rendered_To_Telegram { get; set; }

    /// <summary>Normalizes on load or reload, keeping the resize edge positive.</summary>
    public void Validate()
    {
        if (Resize_To <= 0)
            Resize_To = 128;

        Discord_Target = (Discord_Target ?? string.Empty).Trim();
    }
}

/// <summary>
/// The cross-plugin image handoff contract.
/// Plugins load into separate collectible load contexts, so a plugin-defined message type does not unify between publisher and subscriber; the message is therefore a tagged <see cref="string"/> array, whose element type lives in the shared default context.
/// </summary>
public static class BridgeImageRequest
{
    /// <summary>Element 0 of the message: the versioned tag that identifies this contract.</summary>
    public const string Tag = "mcc.plugins.bridge.image.v1";

    /// <summary>The <c>Target</c> value naming the Discord bridge.</summary>
    public const string TargetDiscord = "discord";

    /// <summary>The <c>Target</c> value naming the Telegram bridge.</summary>
    public const string TargetTelegram = "telegram";

    /// <summary>Builds a request message: [tag, target, absolute file path, caption].</summary>
    public static string[] Create(string target, string filePath, string caption)
        => Create(target, filePath, caption, string.Empty);

    /// <summary>
    /// Builds a request message with a destination: [tag, target, absolute file path, caption, destination].
    /// The destination is a comma-separated list of <c>route:name</c> and <c>dm:owner-id</c> entries; empty means the bridge's default fan-out.
    /// Bridges that predate destinations read the first four elements and ignore the fifth.
    /// </summary>
    public static string[] Create(string target, string filePath, string caption, string destination)
        => [Tag, target, filePath, caption, destination ?? string.Empty];

    /// <summary>Reads a request message addressed to <paramref name="target"/>; false when it is not one.</summary>
    public static bool TryRead(string[]? message, string target, out string filePath, out string caption)
        => TryRead(message, target, out filePath, out caption, out _);

    /// <summary>
    /// Reads a request message with its destination.
    /// Four-element messages predate destinations and read back empty, which means the bridge's default fan-out.
    /// </summary>
    public static bool TryRead(
        string[]? message, string target, out string filePath, out string caption, out string destination)
    {
        filePath = string.Empty;
        caption = string.Empty;
        destination = string.Empty;
        if (message is not { Length: 4 } and not { Length: 5 }
            || !string.Equals(message[0], Tag, StringComparison.Ordinal)
            || !string.Equals(message[1], target, StringComparison.Ordinal))
            return false;

        filePath = message[2];
        caption = message[3];
        destination = message.Length > 4 ? message[4] : string.Empty;
        return true;
    }
}

/// <summary>The Map plugin entry point.</summary>
public sealed class MapPlugin : IPlugin
{
    private const string RenderedMapsFolder = "Rendered_Maps";

    private readonly Dictionary<int, CachedMap> _maps = [];
    private readonly object _gate = new();

    private MapSettings _settings = new();
    private PluginContext? _context;
    private ILogger? _logger;
    private IPluginLocalization? _strings;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.Id = "map";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<MapSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _settings = context.Settings.Load<MapSettings>();
        _context = context;
        _logger = context.Logger;
        _strings = context.Strings;

        if (_settings.Delete_All_On_Unload)
            DeleteRenderedMaps();

        // Register once for the plugin's lifetime, never per session.
        // The plugin command scope takes the command back off the dispatcher on unload, instead of rooting this instance there forever.
        context.Commands.Register(new MapsCommand(this, context.Strings));

        context.SessionStarted += (_, e) => OnSessionStarted(e.Session);
        context.SessionEnded += (_, _) => ClearCache();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeactivateAsync(CancellationToken ct)
    {
        if (_settings.Delete_All_On_Unload)
            DeleteRenderedMaps();

        return Task.CompletedTask;
    }

    private void OnSessionStarted(ISessionScope session)
    {
        ClearCache();

        // The core merges the packet into ClientState.Maps and then raises this, so the event is the signal and the state is the data.
        // Handlers run on the session loop, the only thread that mutates map state, so reading it here is safe.
        session.Events.Subscribe<MapDataReceived>(e =>
        {
            if (session.State.Maps.TryGet(e.MapId, out MapData? map))
                OnMapData(map);
        });
    }

    private void ClearCache()
    {
        lock (_gate)
            _maps.Clear();
    }

    /// <summary>
    /// Snapshots one map update under the plugin's own lock, then notifies or auto-renders.
    /// The copy exists because the command thread renders from it, off the session loop.
    /// </summary>
    private void OnMapData(MapData source)
    {
        if (!_settings.Enabled)
            return;

        CachedMap map;
        bool isFirst;
        lock (_gate)
        {
            isFirst = !_maps.TryGetValue(source.MapId, out CachedMap? existing);
            if (isFirst)
            {
                existing = new CachedMap { MapId = source.MapId };
                _maps[source.MapId] = existing;
            }

            map = existing!;
        }

        map.CopyFrom(source.Colors, MapData.Size, MapData.Size);
        map.Scale = source.Scale;
        map.Locked = source.Locked;
        map.IconCount = source.Icons.Count;
        map.LastUpdated = DateTime.Now;

        if (isFirst && _settings.Notify_On_First_Update)
            _logger?.LogInformation("{Message}", _strings?.Format("received_map", map.MapId));

        if (_settings.Auto_Render_On_Update)
            Render(map, output: null);
    }

    /// <summary>Lists the cached maps in id order.</summary>
    internal int ListMaps(CommandContext ctx, IPluginLocalization strings)
    {
        KeyValuePair<int, CachedMap>[] snapshot;
        lock (_gate)
            snapshot = [.. _maps];

        if (snapshot.Length == 0)
            return ctx.Result.Fail(strings.Get("no_maps"));

        Array.Sort(snapshot, static (a, b) => a.Key.CompareTo(b.Key));
        ctx.Output.WriteLine(strings.Get("received"));
        foreach (KeyValuePair<int, CachedMap> entry in snapshot)
            ctx.Output.WriteLine(strings.Format("list_item", entry.Key, entry.Value.LastUpdated));

        return ctx.Result.Ok();
    }

    /// <summary>Renders one cached map on command.</summary>
    internal int RenderMap(CommandContext ctx, int mapId, IPluginLocalization strings)
    {
        CachedMap? map;
        lock (_gate)
            _maps.TryGetValue(mapId, out map);

        if (map is null)
            return ctx.Result.Fail(strings.Format("not_found", mapId));

        return Render(map, ctx.Output)
            ? ctx.Result.Ok()
            : ctx.Result.Fail(strings.Format("failed_to_render", mapId));
    }

    /// <summary>The shared render path: optional file write, optional console print.</summary>
    private bool Render(CachedMap map, ICommandOutput? output)
    {
        try
        {
            byte[] colors = map.SnapshotColors();
            if (colors.Length == 0 || map.Width == 0 || map.Height == 0)
                return false;

            RgbImage image = RgbImage.FromMapColors(colors, map.Width, map.Height);

            if (_settings.Save_To_File)
                SaveToFile(map, image);
            else if ((_settings.Send_Rendered_To_Discord || _settings.Send_Rendered_To_Telegram)
                && _context is { } context)
                // Sending to a bridge must not require saving bitmaps: hand over without the file.
                SendToBridges(context, map, SizeForOutput(image));

            if (_settings.Render_In_Console)
                RenderInConsole(image, output);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger?.LogWarning(ex, "{Message}", _strings?.Format("failed_to_render", map.MapId));
            return false;
        }
    }

    /// <summary>
    /// Scales the render to <c>Resize_To</c> when resizing is on, for files and bridges alike.
    /// Nearest neighbor keeps the chunky map pixels instead of blurring them.
    /// </summary>
    private RgbImage SizeForOutput(RgbImage image)
        => _settings.Rasize_Rendered_Image
            ? image.ResizeNearest(_settings.Resize_To, _settings.Resize_To)
            : image;

    private void SaveToFile(CachedMap map, RgbImage image)
    {
        if (_context is not { } context)
            return;

        RgbImage saved = SizeForOutput(image);

        string name = string.Format(
            CultureInfo.InvariantCulture, "Map_{0}.bmp", map.MapId.ToString("D5", CultureInfo.InvariantCulture));
        string path = context.Storage.GetPath(Path.Combine(RenderedMapsFolder, name));
        File.WriteAllBytes(path, BmpEncoder.Encode(saved));
        _logger?.LogInformation("{Message}", _strings?.Format("rendered", map.MapId, path));

        if (_settings.Rasize_Rendered_Image)
            _logger?.LogInformation("{Message}", _strings?.Format("resized", map.MapId, _settings.Resize_To));

        if (_settings.Send_Rendered_To_Discord || _settings.Send_Rendered_To_Telegram)
            SendToBridges(context, map, saved);
    }

    /// <summary>
    /// Hands the render to the bridge plugins as a PNG, since Discord and Telegram both reject bitmaps.
    /// The bridges buffer it themselves, because only they know when their connection is up.
    /// </summary>
    private void SendToBridges(PluginContext context, CachedMap map, RgbImage image)
    {
        string pngName = string.Format(
            CultureInfo.InvariantCulture, "Map_{0}.png", map.MapId.ToString("D5", CultureInfo.InvariantCulture));
        string pngPath = context.Storage.GetPath(Path.Combine(RenderedMapsFolder, pngName));
        File.WriteAllBytes(pngPath, PngEncoder.Encode(image));

        if (_settings.Send_Rendered_To_Discord)
        {
            context.Messenger.Publish(BridgeImageRequest.Create(
                BridgeImageRequest.TargetDiscord,
                pngPath,
                _strings?.Format("discord_caption", map.MapId) ?? string.Empty,
                _settings.Discord_Target ?? string.Empty));
            _logger?.LogInformation("{Message}", _strings?.Format("sent_to_discord", map.MapId));
        }

        if (_settings.Send_Rendered_To_Telegram)
        {
            context.Messenger.Publish(BridgeImageRequest.Create(
                BridgeImageRequest.TargetTelegram,
                pngPath,
                _strings?.Format("telegram_caption", map.MapId) ?? string.Empty));
            _logger?.LogInformation("{Message}", _strings?.Format("sent_to_telegram", map.MapId));
        }
    }

    private void RenderInConsole(RgbImage image, ICommandOutput? output)
    {
        var request = new ImagePresentationRequest(new RgbImageFrame(image.Width, image.Height, image.Pixels), Output: output);
        if (_context?.Client.HostUi?.TryPresentImage(request) != true)
            Emit(_strings?.Get("image_unavailable") ?? string.Empty, output);
    }

    /// <summary>Command output when the render was asked for, the plugin log otherwise.</summary>
    private void Emit(string line, ICommandOutput? output)
    {
        if (output is not null)
        {
            output.WriteLine(line);
            return;
        }

        _logger?.LogInformation("{Line}", line);
    }

    private void DeleteRenderedMaps()
    {
        if (_context is not { } context)
            return;

        try
        {
            string folder = Path.Combine(context.Storage.DataDirectory, RenderedMapsFolder);
            if (!Directory.Exists(folder))
                return;

            foreach (string file in Directory.EnumerateFiles(folder))
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("cleanup_failed"));
        }
    }
}

/// <summary>The <c>maps</c> command.</summary>
internal sealed class MapsCommand(MapPlugin plugin, IPluginLocalization strings) : CommandBase
{
    private readonly MapPlugin _plugin = plugin;
    private readonly IPluginLocalization _strings = strings;

    /// <inheritdoc/>
    public override string CmdName => "maps";

    /// <inheritdoc/>
    public override string CmdDesc => _strings.Get("command_desc");

    /// <inheritdoc/>
    public override string CmdUsage => _strings.Get("command_usage");

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Plugins;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("list", _strings.Get("usage_list")),
        new("render <id>", _strings.Get("usage_render")),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["maps list", "maps render 3"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["inventory"];

    /// <inheritdoc/>
    public override string? ManTopic => "map";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        builder.Literal(CmdName, l => l
            .Executes(ctx => _plugin.ListMaps(ctx.Source, _strings))
            .ThenLiteral("list", h => h
                .Executes(ctx => _plugin.ListMaps(ctx.Source, _strings)))
            .ThenLiteral("render", h => h
                .ThenArgument("MapID", Arguments.Integer(), a => a
                    .Executes(ctx => _plugin.RenderMap(
                        ctx.Source, ctx.GetArgument<int>("MapID"), _strings))))
            .ThenLiteral("_help", h => h
                .Executes(ctx => ShowUsage(ctx.Source))
                .RedirectTo(help)));
    }
}
