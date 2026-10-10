// PlayerListLogger: every Delay seconds it appends the tab list to a file inside the plugin's data/ folder.
// Rounds where no session is attached are skipped rather than logged as empty.

using System.Globalization;
using System.Text;
using DMCBK.Core;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;

namespace DMCBK.OfficialPlugins.PlayerListLogger;

/// <summary>The record formatting, kept free of session state so it can be tested.</summary>
public static class PlayerListRecord
{
    /// <summary>A delay below one second becomes one second.</summary>
    public static double ClampDelay(double delaySeconds) => delaySeconds < 1.0 ? 1.0 : delaySeconds;

    /// <summary>
    /// Builds one log record: the unpadded "[y/M/d H:m]" header, the joined player names, then a blank line.
    /// Names are written in the order given.
    /// </summary>
    public static string Build(DateTime now, IEnumerable<string> players)
    {
        ArgumentNullException.ThrowIfNull(players);
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"[{now.Year}/{now.Month}/{now.Day} {now.Hour}:{now.Minute}]");
        builder.Append('\n');
        builder.Append(string.Join(", ", players));
        builder.Append('\n');
        builder.Append('\n');
        return builder.ToString().Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }
}

/// <summary>The settings for PlayerListLogger, saved to settings.toml.</summary>
public sealed class PlayerListLoggerSettings : IValidatablePluginSettings
{
    /// <summary>Master switch.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; }

    /// <summary>The log file, resolved inside the plugin's data/ folder.</summary>
    [TomlInlineComment("$settings.file$")]
    public string File { get; set; } = "playerlog.txt";

    /// <summary>Seconds between two records. Clamped to at least 1.</summary>
    [TomlInlineComment("$settings.delay$")]
    public double Delay { get; set; } = 60;

    /// <summary>Normalizes on load and reload.</summary>
    public void Validate()
    {
        File ??= string.Empty;
        if (File.Trim().Length == 0)
            File = "playerlog.txt";

        Delay = PlayerListRecord.ClampDelay(Delay);
    }
}

/// <summary>The PlayerListLogger plugin entry point.</summary>
public sealed class PlayerListLoggerPlugin : IPlugin
{
    private readonly object _fileGate = new();
    private PlayerListLoggerSettings _settings = new();
    private IDisposable? _job;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "player-list-logger";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<PlayerListLoggerSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _settings = context.Settings.Load<PlayerListLoggerSettings>();
        if (!_settings.Enabled)
            return Task.CompletedTask;

        // Wall-clock cadence, so the period survives a reconnect.
        // The host disposes the plugin's cron registrations on unload, so this handle needs no teardown of its own.
        _job = context.Cron.Every(TimeSpan.FromSeconds(_settings.Delay), () => _ = SaveAsync(context));
        return Task.CompletedTask;
    }

    /// <summary>Whether the periodic job is armed (diagnostics and tests).</summary>
    public bool IsRunning => _job is not null;

    private async Task SaveAsync(PluginContext context)
    {
        if (!_settings.Enabled || !context.InSession)
            return;

        context.Logger.LogDebug("{Message}", context.Strings.Get("saving"));

        TabListSnapshot snapshot;
        try
        {
            snapshot = await context.Game.Player.GetTabListAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.LogDebug(ex, "{Message}", context.Strings.Get("read_failed"));
            return;
        }

        var names = new List<string>(snapshot.Entries.Count);
        foreach (TabListEntryInfo entry in snapshot.Entries)
        {
            if (!string.IsNullOrEmpty(entry.Name))
                names.Add(entry.Name);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        string record = PlayerListRecord.Build(DateTime.Now, names);

        try
        {
            string path = context.Storage.GetPath(_settings.File);
            lock (_fileGate)
                System.IO.File.AppendAllText(path, record);

            context.Logger.LogInformation("{Message}", context.Strings.Format("saved", names.Count, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            context.Logger.LogWarning(ex, "{Message}", context.Strings.Format("write_failed", _settings.File));
        }
    }
}
