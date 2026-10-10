// ScriptScheduler - runs a configured command on a trigger: the first login of a run, every login, given times of day, or a random interval between two bounds.
//
// An action starting with "script " runs as a command; the core has no C# script action.
// Such an action is dispatched like any other command and reports the core's own "removed" message.

using System.Globalization;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;

namespace DMCBK.OfficialPlugins.ScriptScheduler;

/// <summary>The time-of-day trigger of one task.</summary>
public sealed class TriggerOnTimeConfig
{
    /// <summary>Whether the trigger is active.</summary>
    [TomlInlineComment("$settings.trigger_on_times$")]
    public bool Enable { get; set; }

    /// <summary>The times of day, as "HH:mm:ss" (or "HH:mm") strings.</summary>
    [TomlInlineComment("$settings.times$")]
    public string[] Times { get; set; } = [];
}

/// <summary>The interval trigger of one task.</summary>
public sealed class TriggerOnIntervalConfig
{
    /// <summary>Whether the trigger is active.</summary>
    [TomlInlineComment("$settings.trigger_on_interval$")]
    public bool Enable { get; set; }

    /// <summary>The minimum delay in seconds.</summary>
    public double MinTime { get; set; } = 1;

    /// <summary>The maximum delay in seconds.</summary>
    public double MaxTime { get; set; } = 10;
}

/// <summary>One scheduled task.</summary>
public sealed class TaskConfig
{
    /// <summary>A free-form name used in the diagnostics.</summary>
    public string Task_Name { get; set; } = "Task Name (Can be empty)";

    /// <summary>Fire once, on the first join of this client run.</summary>
    public bool Trigger_On_First_Login { get; set; }

    /// <summary>Fire on every join, reconnects included.</summary>
    public bool Trigger_On_Login { get; set; }

    /// <summary>
    /// Fire when the server shows a dialog (1.21.6+), in configuration or in play.
    /// This is the trigger for login dialogs that appear before the join finishes, where <see cref="Trigger_On_Login"/> never fires.
    /// </summary>
    [TomlInlineComment("$settings.trigger_on_dialog$")]
    public bool Trigger_On_Dialog { get; set; }

    /// <summary>
    /// Optional dialog filter: only fire when the dialog title or one of its input keys contains this text (case-insensitive).
    /// Empty matches every dialog.
    /// </summary>
    [TomlInlineComment("$settings.dialog_match$")]
    public string Dialog_Match { get; set; } = string.Empty;

    /// <summary>The time-of-day trigger.</summary>
    public TriggerOnTimeConfig Trigger_On_Times { get; set; } = new();

    /// <summary>The interval trigger.</summary>
    public TriggerOnIntervalConfig Trigger_On_Interval { get; set; } = new();

    /// <summary>The internal command to run.</summary>
    public string Action { get; set; } = "send /hello";
}

/// <summary>The scheduling decisions of ScriptScheduler.</summary>
public static class ScheduleRules
{
    /// <summary>The floor for an interval bound, in seconds.</summary>
    public const double MinimumInterval = 0.1;

    /// <summary>Raises each bound to the floor and swaps an inverted range.</summary>
    public static (double Min, double Max) ClampInterval(double min, double max)
    {
        double low = Math.Max(MinimumInterval, min);
        double high = Math.Max(MinimumInterval, max);
        return low > high ? (high, low) : (low, high);
    }

    /// <summary>Parses a configured time of day; accepts "HH:mm" and "HH:mm:ss".</summary>
    public static bool TryParseTime(string? value, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string text = value.Trim();
        if (TimeOnly.TryParseExact(text, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
            return true;

        if (TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
            return true;

        // A time written as a TOML local time round trips as a TimeSpan-shaped string, so accept that too.
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan span)
            && span >= TimeSpan.Zero
            && span < TimeSpan.FromDays(1))
        {
            time = TimeOnly.FromTimeSpan(span);
            return true;
        }

        return false;
    }

    /// <summary>True when the task has at least one usable trigger.</summary>
    public static bool HasTrigger(TaskConfig task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Trigger_On_Login || task.Trigger_On_First_Login || task.Trigger_On_Dialog)
            return true;

        if (task.Trigger_On_Times.Enable && ValidTimes(task.Trigger_On_Times.Times).Count > 0)
            return true;

        return task.Trigger_On_Interval.Enable && task.Trigger_On_Interval.MinTime > 0;
    }

    /// <summary>True when the task has an action to run.</summary>
    public static bool HasAction(TaskConfig task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return !string.IsNullOrWhiteSpace(task.Action);
    }

    /// <summary>The parseable subset of a configured time list, in order.</summary>
    public static IReadOnlyList<TimeOnly> ValidTimes(IEnumerable<string>? times)
    {
        var result = new List<TimeOnly>();
        if (times is null)
            return result;

        foreach (string value in times)
        {
            if (TryParseTime(value, out TimeOnly time))
                result.Add(time);
        }

        return result;
    }

    /// <summary>Whether a task should run on this join.</summary>
    public static bool ShouldRunOnLogin(TaskConfig task, bool isFirstLogin)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.Trigger_On_Login || (isFirstLogin && task.Trigger_On_First_Login);
    }

    /// <summary>
    /// Whether a task should run for the shown dialog.
    /// An empty <see cref="TaskConfig.Dialog_Match"/> matches every dialog; otherwise the dialog title or one of its input keys must contain the text (case-insensitive).
    /// </summary>
    public static bool ShouldRunOnDialog(TaskConfig task, string title, IReadOnlyList<string> inputKeys)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(inputKeys);
        if (!task.Trigger_On_Dialog)
            return false;

        if (string.IsNullOrWhiteSpace(task.Dialog_Match))
            return true;

        string needle = task.Dialog_Match.Trim();
        if (!string.IsNullOrEmpty(title)
            && title.Contains(needle, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (string key in inputKeys)
        {
            if (!string.IsNullOrEmpty(key)
                && key.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>A one-line description of a task, used in the load-time diagnostics.</summary>
    public static string Describe(TaskConfig task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return string.Format(
            CultureInfo.InvariantCulture,
            "name='{0}' first_login={1} login={2} dialog={3} dialog_match='{4}' times={5} [{6}] interval={7} [{8}-{9}] action='{10}'",
            task.Task_Name,
            task.Trigger_On_First_Login,
            task.Trigger_On_Login,
            task.Trigger_On_Dialog,
            task.Dialog_Match ?? string.Empty,
            task.Trigger_On_Times.Enable,
            string.Join(", ", task.Trigger_On_Times.Times ?? []),
            task.Trigger_On_Interval.Enable,
            task.Trigger_On_Interval.MinTime,
            task.Trigger_On_Interval.MaxTime,
            task.Action);
    }
}


/// <summary>The persisted settings for ScriptScheduler.</summary>
public sealed class ScriptSchedulerSettings : IValidatablePluginSettings
{
    /// <summary>Master switch.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; }

    /// <summary>The configured tasks.</summary>
    [TomlPrecedingComment("$settings.task_list$")]
    public TaskConfig[] TaskList { get; set; } = [];

    /// <summary>Normalizes the task list on load or reload.</summary>
    public void Validate()
    {
        TaskList ??= [];
        foreach (TaskConfig task in TaskList)
        {
            task.Task_Name ??= string.Empty;
            task.Action ??= string.Empty;
            task.Dialog_Match ??= string.Empty;
            task.Trigger_On_Times ??= new TriggerOnTimeConfig();
            task.Trigger_On_Times.Times ??= [];
            task.Trigger_On_Interval ??= new TriggerOnIntervalConfig();

            (double min, double max) = ScheduleRules.ClampInterval(
                task.Trigger_On_Interval.MinTime, task.Trigger_On_Interval.MaxTime);
            task.Trigger_On_Interval.MinTime = min;
            task.Trigger_On_Interval.MaxTime = max;
        }
    }
}

/// <summary>The ScriptScheduler plugin entry point.</summary>
public sealed class ScriptSchedulerPlugin : IPlugin
{
    private readonly List<IDisposable> _jobs = [];
    private ScriptSchedulerSettings _settings = new();
    private TaskConfig[] _tasks = [];
    private int _firstLoginDone;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "script-scheduler";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<ScriptSchedulerSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _settings = context.Settings.Load<ScriptSchedulerSettings>();
        if (!_settings.Enabled)
            return Task.CompletedTask;

        _tasks = SelectUsableTasks(context, _settings.TaskList);
        if (_tasks.Length == 0)
        {
            context.Logger.LogWarning("{Message}", context.Strings.Get("no_tasks"));
            return Task.CompletedTask;
        }

        RegisterCronJobs(context);

        context.SessionStarted += (_, _) => OnSessionStarted(context);
        context.SessionCreated += (_, e) => OnSessionCreated(context, e.Session);
        return Task.CompletedTask;
    }

    /// <summary>The tasks that survived validation.</summary>
    public IReadOnlyList<TaskConfig> Tasks => _tasks;

    /// <summary>The number of armed cron registrations.</summary>
    public int JobCount
    {
        get
        {
            lock (_jobs)
                return _jobs.Count;
        }
    }

    private static TaskConfig[] SelectUsableTasks(PluginContext context, TaskConfig[] tasks)
    {
        var usable = new List<TaskConfig>();
        foreach (TaskConfig task in tasks ?? [])
        {
            string described = ScheduleRules.Describe(task);
            if (!ScheduleRules.HasAction(task))
            {
                context.Logger.LogDebug("{Message}", context.Strings.Format("no_action", described));
                continue;
            }

            if (!ScheduleRules.HasTrigger(task))
            {
                context.Logger.LogDebug("{Message}", context.Strings.Format("no_trigger", described));
                continue;
            }

            context.Logger.LogDebug("{Message}", context.Strings.Format("loaded_task", described));
            usable.Add(task);
        }

        return [.. usable];
    }

    private void RegisterCronJobs(PluginContext context)
    {
        foreach (TaskConfig task in _tasks)
        {
            TaskConfig captured = task;

            if (task.Trigger_On_Times.Enable)
            {
                foreach (string value in task.Trigger_On_Times.Times)
                {
                    if (!ScheduleRules.TryParseTime(value, out TimeOnly time))
                    {
                        context.Logger.LogWarning("{Message}", context.Strings.Format("bad_time", value ?? string.Empty));
                        continue;
                    }

                    Track(context.Cron.DailyAt(
                        time,
                        () => Fire(context, captured, context.Strings.Format("running_time", captured.Action))));
                }
            }

            if (task.Trigger_On_Interval.Enable && task.Trigger_On_Interval.MinTime > 0)
            {
                (double min, double max) = ScheduleRules.ClampInterval(
                    task.Trigger_On_Interval.MinTime, task.Trigger_On_Interval.MaxTime);
                Track(context.Cron.EveryWithJitter(
                    TimeSpan.FromSeconds(min),
                    TimeSpan.FromSeconds(max),
                    () => Fire(context, captured, context.Strings.Format("running_interval", captured.Action))));
            }
        }
    }

    private void Track(IDisposable job)
    {
        lock (_jobs)
            _jobs.Add(job);
    }

    private void OnSessionStarted(PluginContext context)
    {
        bool isFirstLogin = Interlocked.Exchange(ref _firstLoginDone, 1) == 0;
        foreach (TaskConfig task in _tasks)
        {
            if (ScheduleRules.ShouldRunOnLogin(task, isFirstLogin))
                Fire(context, task, context.Strings.Format("running_login", task.Action));
        }
    }

    private void OnSessionCreated(PluginContext context, IPreSessionScope session)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);
        // Subscribed before the dial, so configuration-phase dialogs are seen.
        // The handler only snapshots and hops off-loop; see FireDialog for why.
        session.SubscribeDialog(_ => OnDialogShown(context, session));
    }

    private void OnDialogShown(PluginContext context, IPreSessionScope session)
    {
        PreSessionDialogInfo? dialog;
        try
        {
            dialog = session.GetDialog();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.LogDebug(ex, "{Message}", context.Strings.Get("dialog_snapshot_failed"));
            return;
        }

        if (dialog is null)
            return;

        List<TaskConfig> matching = [];
        foreach (TaskConfig task in _tasks)
        {
            if (ScheduleRules.ShouldRunOnDialog(task, dialog.Title, dialog.InputKeys))
                matching.Add(task);
        }

        if (matching.Count == 0)
            return;

        // One background task running the matches in list order: a set-then-click pair listed in order dispatches in order, which two racing tasks would not.
        // No InSession gate: dialogs arrive during configuration, before SessionStarted.
        _ = Task.Run(() => RunDialogAsync(context, matching));
    }

    private static async Task RunDialogAsync(PluginContext context, IReadOnlyList<TaskConfig> matching)
    {
        foreach (TaskConfig task in matching)
        {
            context.Logger.LogDebug("{Message}", context.Strings.Format("running_dialog", task.Action));
            await RunAsync(context, task.Action).ConfigureAwait(false);
        }
    }

    private static void Fire(PluginContext context, TaskConfig task, string debugMessage)
    {
        // Cron fires on wall-clock time whether or not a session is attached; tasks only run while logged in.
        if (!context.InSession)
            return;

        context.Logger.LogDebug("{Message}", debugMessage);

        // Always dispatch OFF the session loop.
        // Cron callbacks already run on the thread pool, but the login trigger fires from SessionStarted, which is raised on the loop, and CommandContext.Run is sync-over-async: dispatching inline there deadlocks the session until the server times it out.
        string action = task.Action;
        _ = Task.Run(() => RunAsync(context, action));
    }

    private static async Task RunAsync(PluginContext context, string action)
    {
        string command = context.Variables.Expand(action);
        try
        {
            CmdResult result = await context.Client.Commands.DispatchAsync(command).ConfigureAwait(false);
            if (result.Status != CmdStatus.Done || !string.IsNullOrWhiteSpace(result.Message))
            {
                context.Logger.LogInformation(
                    "{Message}", context.Strings.Format("result", result.Message ?? string.Empty));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.LogWarning(ex, "{Message}", context.Strings.Format("result", command));
        }
    }
}
