using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Umpk.Client.Events;

namespace DMCBK.OfficialPlugins.McpServer;

/// <summary>
/// The MCP server plugin entry point.
/// Owns the HTTP listener (started in <see cref="ActivateAsync"/> when enabled, stopped in <see cref="BeforeExit"/> while the session is still live and disposed in <see cref="DeactivateAsync"/>) and the per-session observed-state feed.
/// The listener stays up across reconnects so agents keep one stable endpoint; session-dependent tools report <c>disconnected</c> while offline and the stores clear on every session end.
/// </summary>
public sealed class McpServerPlugin : IPlugin
{
    private PluginContext? _context;
    private McpServerSettings _settings = new();
    private ObservedStateStore? _store;
    private McpCapabilities? _capabilities;
    private McpGuidance? _guidance;
    private EmbeddedMcpHost? _host;
    private IDisposable? _commandHandle;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.Id = "mcp-server";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<McpServerSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _settings = context.Settings.Load<McpServerSettings>();
        _store = new ObservedStateStore();
        _guidance = new McpGuidance();
        _capabilities = new McpCapabilities(context, _store, _guidance);

        context.SessionStarted += (_, e) => OnSessionStarted(e.Session);
        context.SessionEnded += (_, _) => _store?.Clear();
        context.BeforeExit += (_, e) => e.Defer(StopAsync());

        _commandHandle = context.Commands.Register(new McpStatusCommand(this));

        if (_settings.Enabled)
            Start();
        else
            context.Logger.LogInformation("{Message}", context.Strings.Get("disabled"));

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeactivateAsync(CancellationToken ct)
    {
        _commandHandle?.Dispose();
        _commandHandle = null;
        Stop();
        return Task.CompletedTask;
    }

    internal bool IsRunning => _host?.IsRunning ?? false;

    internal string Endpoint
        => _host?.Endpoint ?? $"http://{_settings.BindHost}:{_settings.Port}{_settings.Route}";

    internal void Start()
    {
        if (_context is null || _capabilities is null || _guidance is null)
            return;

        _host ??= new EmbeddedMcpHost(_settings, _capabilities, _guidance, _context.Logger);
        if (!_host.Start(out string? error))
        {
            _context.Logger.LogWarning("{Message}", _context.Strings.Format("start_failed", error ?? "unknown"));
            return;
        }

        _context.Logger.LogInformation("{Message}", _context.Strings.Format("started", Endpoint));
    }

    internal void Stop()
    {
        if (_host is null)
            return;

        if (!_host.Stop(out string? error))
            _context?.Logger.LogWarning("{Message}", _context.Strings.Format("stop_failed", error ?? "unknown"));
        else
            _context?.Logger.LogInformation("{Message}", _context.Strings.Get("stopped"));
    }

    private Task StopAsync()
    {
        Stop();
        return Task.CompletedTask;
    }

    private void OnSessionStarted(ISessionScope session)
    {
        if (_context is null || _store is null)
            return;

        ObservedStateStore store = _store;
        PluginContext context = _context;

        session.Events.Subscribe<ChatMessageReceived>(message => IngestChat(context, store, message));
        session.Events.Subscribe<ChatMessageSuppressed>(_ => store.AddEvent("chat_suppressed"));
        session.Events.Subscribe<ChatStreamGap>(gap => store.AddEvent("chat_gap", new
        {
            expectedIndex = gap.ExpectedIndex,
            actualIndex = gap.ActualIndex,
            totalGaps = gap.TotalGaps,
        }));
        session.Events.Subscribe<ContainerOpened>(e => store.AddEvent("inventory_open", new { inventoryId = e.WindowId }));
        session.Events.Subscribe<ContainerClosed>(e => store.AddEvent("inventory_close", new { inventoryId = e.WindowId }));
        session.Events.Subscribe<Died>(_ => store.AddEvent("death"));
        session.Events.Subscribe<Respawned>(_ => store.AddEvent("respawn"));
        session.Events.Subscribe<TitleChanged>(e => store.AddEvent("title", new
        {
            text = e.Text.ToPlainText(context.Translations),
        }));
        session.Events.Subscribe<SubtitleChanged>(e => store.AddEvent("title", new
        {
            subtitle = e.Text.ToPlainText(context.Translations),
        }));
        session.Events.Subscribe<ActionBarChanged>(e => store.AddEvent("actionbar", new
        {
            text = e.Text.ToPlainText(context.Translations),
        }));
        session.Events.Subscribe<TimeChanged>(e =>
        {
            store.SetTime(e.WorldAge, e.TimeOfDay);
            store.AddEvent("time", new { worldAge = e.WorldAge, timeOfDay = e.TimeOfDay });
        });
        session.Events.Subscribe<RainLevelChanged>(e => store.AddEvent(
            "weather_rain", new { level = e.Level, raining = e.Raining }));
        session.Events.Subscribe<ThunderLevelChanged>(e => store.AddEvent(
            "weather_thunder", new { level = e.Level }));
    }

    private static void IngestChat(PluginContext context, ObservedStateStore store, ChatMessageReceived message)
    {
        string text = message.Message.ToPlainText(context.Translations);
        if (string.IsNullOrWhiteSpace(text))
            return;

        ClassifiedChat classified;
        try
        {
            classified = context.Chat.Classify(message);
        }
        catch (Exception)
        {
            return;
        }

        string kind = classified.Kind switch
        {
            ChatKind.Chat => "chat",
            ChatKind.PrivateMessage => "private",
            _ => "system",
        };
        store.AddChat(kind, text, classified.Sender, classified.Body);
    }
}
