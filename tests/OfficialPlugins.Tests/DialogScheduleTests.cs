using DMCBK.OfficialPlugins.ScriptScheduler;
using Xunit;
namespace DMCBK.Core.Tests;

public sealed class DialogScheduleTests
{
    #region scheduler trigger rules

    [Fact]
    public void Scheduler_HasTrigger_AcceptsDialog()
    {
        Assert.True(ScheduleRules.HasTrigger(
            new TaskConfig { Trigger_On_Dialog = true }));
    }

    [Fact]
    public void Scheduler_ShouldRunOnDialog_MatchesTitleOrKey()
    {
        var any = new TaskConfig { Trigger_On_Dialog = true };
        var filtered = new TaskConfig
        {
            Trigger_On_Dialog = true,
            Dialog_Match = "auth_login",
        };
        var off = new TaskConfig();

        Assert.True(ScheduleRules.ShouldRunOnDialog(any, "Login", ["other"]));
        Assert.True(ScheduleRules.ShouldRunOnDialog(
            filtered, "Something", ["auth_login_password"]));
        Assert.True(ScheduleRules.ShouldRunOnDialog(
            filtered, "Auth_Login wall", ["other"]));
        Assert.False(ScheduleRules.ShouldRunOnDialog(
            filtered, "News", ["other"]));
        Assert.False(ScheduleRules.ShouldRunOnDialog(off, "Login", ["auth_login_password"]));
    }

    [Fact]
    public void Scheduler_Validate_NormalizesDialogMatch()
    {
        var settings = new ScriptSchedulerSettings
        {
            TaskList = [new TaskConfig { Dialog_Match = null! }],
        };
        settings.Validate();
        Assert.Equal(string.Empty, settings.TaskList[0].Dialog_Match);
    }

    #endregion
}
