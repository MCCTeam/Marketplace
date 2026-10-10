using DMCBK.OfficialPlugins.McpServer;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>The tool-result envelope contract.</summary>
public sealed class McpResultTests
{
    [Fact]
    public void Ok_SetsSuccessAndData()
    {
        var data = new { a = 1 };
        McpResult result = McpResult.Ok(data, "hello");
        Assert.True(result.Success);
        Assert.Same(data, result.Data);
        Assert.Equal("hello", result.Message);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Fail_SetsCodeAndMessage()
    {
        McpResult result = McpResult.Fail(McpErrorCodes.Disconnected, "gone");
        Assert.False(result.Success);
        Assert.Equal(McpErrorCodes.Disconnected, result.ErrorCode);
        Assert.Equal("gone", result.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Fail_RejectsMissingCode(string? code)
    {
        Assert.ThrowsAny<ArgumentException>(() => McpResult.Fail(code!));
    }

    [Fact]
    public void ErrorCodes_AreStable()
    {
        Assert.Equal("disconnected", McpErrorCodes.Disconnected);
        Assert.Equal("capability_disabled", McpErrorCodes.CapabilityDisabled);
        Assert.Equal("feature_disabled", McpErrorCodes.FeatureDisabled);
        Assert.Equal("invalid_args", McpErrorCodes.InvalidArgs);
        Assert.Equal("action_failed", McpErrorCodes.ActionFailed);
        Assert.Equal("action_incomplete", McpErrorCodes.ActionIncomplete);
        Assert.Equal("movement_held", McpErrorCodes.MovementHeld);
    }
}
