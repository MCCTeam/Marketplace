using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DMCBK.OfficialPlugins.McpServer;

/// <summary>
/// The Streamable-HTTP host for the MCP server: a Kestrel <see cref="WebApplication"/> serving <c>MapMcp(route)</c> with the tool and prompt sets, plus an all-or-nothing Bearer gate over the route when auth is required.
/// The host applies three hardening rules: bind failures return error codes instead of throwing, a dead listener never reports healthy, and start/stop are idempotent so reload cannot wedge on address-in-use.
/// </summary>
public sealed class EmbeddedMcpHost
{
    private readonly McpServerSettings _settings;
    private readonly McpCapabilities _capabilities;
    private readonly McpGuidance _guidance;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    private readonly object _gate = new();
    private WebApplication? _app;
    private int _inflight;

    public EmbeddedMcpHost(
        McpServerSettings settings,
        McpCapabilities capabilities,
        McpGuidance guidance,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        _settings = settings;
        _capabilities = capabilities;
        _guidance = guidance;
        _logger = logger;
    }

    /// <summary>True while the listener is up.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _app is not null;
        }
    }

    /// <summary>How many tool calls are executing right now.</summary>
    public int Inflight => Volatile.Read(ref _inflight);

    /// <summary>The endpoint URL agents connect to.</summary>
    public string Endpoint => $"http://{_settings.BindHost}:{_settings.Port}{NormalizeRoute(_settings.Route)}";

    /// <summary>Starts the listener. Idempotent: a running listener returns true immediately.</summary>
    public bool Start(out string? error)
    {
        lock (_gate)
        {
            error = null;
            if (_app is not null)
                return true;

            string route = NormalizeRoute(_settings.Route);
            string bindHost = string.IsNullOrWhiteSpace(_settings.BindHost)
                ? McpServerSettings.DefaultBindHost
                : _settings.BindHost.Trim();
            if (_settings.Port is < 1 or > 65535)
            {
                error = McpErrorCodes.InvalidPort;
                return false;
            }

            string? requiredToken = null;
            if (_settings.RequireAuthToken)
            {
                requiredToken = McpAuth.ResolveRequiredToken(_settings.AuthTokenEnvVar, out error);
                if (requiredToken is null)
                    return false;
            }

            if (!McpServerSettings.IsLoopback(bindHost)
                && (!_settings.RequireAuthToken || !_settings.AllowNonLoopback))
            {
                error = McpErrorCodes.BindFailed;
                return false;
            }

            if (!IPAddress.TryParse(bindHost, out _) && !string.Equals(bindHost, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                error = McpErrorCodes.BindFailed;
                return false;
            }

            try
            {
                WebApplicationBuilder builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.Logging.AddFilter(_ => false);
                builder.Services.AddSingleton(_capabilities);
                builder.Services.AddSingleton(_guidance);
                builder.Services.AddSingleton<McpToolSet>();
                builder.Services.AddSingleton<McpPromptSet>();
                builder.Services.AddMcpServer()
                    .WithHttpTransport()
                    .WithTools<McpToolSet>()
                    .WithPrompts<McpPromptSet>();

                builder.WebHost.UseUrls($"http://{bindHost}:{_settings.Port}");
                WebApplication app = builder.Build();

                app.Use(async (context, next) =>
                {
                    bool tracked = McpAuth.IsUnderRoute(context.Request.Path.ToString(), route);
                    if (tracked)
                        TrackStart();

                    try
                    {
                        await next().ConfigureAwait(false);
                    }
                    finally
                    {
                        if (tracked)
                            TrackStop();
                    }
                });

                if (_settings.RequireAuthToken)
                {
                    string token = requiredToken!;
                    app.Use(async (context, next) =>
                    {
                        if (McpAuth.IsUnderRoute(context.Request.Path.ToString(), route))
                        {
                            string auth = context.Request.Headers.Authorization.ToString();
                            if (!McpAuth.IsAuthorized(
                                context.Request.Path.ToString(), auth, route, token))
                            {
                                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                await context.Response.WriteAsync("Unauthorized").ConfigureAwait(false);
                                return;
                            }
                        }

                        await next().ConfigureAwait(false);
                    });
                }

                app.MapMcp(route);
                app.StartAsync().GetAwaiter().GetResult();
                _app = app;
                return true;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "MCP endpoint failed to bind {Endpoint}.", Endpoint);
                error = McpErrorCodes.BindFailed;
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP endpoint failed to start {Endpoint}.", Endpoint);
                error = McpErrorCodes.BindFailed;
                return false;
            }
        }
    }

    /// <summary>
    /// Stops the listener, waiting briefly for in-flight tools to drain.
    /// Idempotent.
    /// On failure the handle is still cleared so the next <see cref="Start"/> binds fresh instead of reporting a healthy dead listener.
    /// </summary>
    public bool Stop(out string? error)
    {
        WebApplication? app;
        lock (_gate)
        {
            error = null;
            app = _app;
            _app = null;
            if (app is null)
                return true;
        }

        try
        {
            // Bounded drain: tools that outlive this still fail on their own linked tokens.
            SpinWait.SpinUntil(() => Volatile.Read(ref _inflight) == 0, TimeSpan.FromSeconds(2));
            app.StopAsync().GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception)
        {
            error = McpErrorCodes.StopFailed;
            return false;
        }
    }

    internal void TrackStart() => Interlocked.Increment(ref _inflight);

    internal void TrackStop() => Interlocked.Decrement(ref _inflight);

    internal static string NormalizeRoute(string route)
    {
        string normalized = string.IsNullOrWhiteSpace(route) ? "/mcp" : route.Trim();
        if (!normalized.StartsWith('/'))
            normalized = '/' + normalized;

        if (normalized.Length > 1)
            normalized = normalized.TrimEnd('/');

        return normalized;
    }
}
