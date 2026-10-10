using DMCBK.OfficialPlugins.McpServer;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>Settings normalization: ports, hosts, routes, and the non-loopback fallback.</summary>
public sealed class McpServerSettingsTests
{
    [Fact]
    public void Validate_ResetsBadPort()
    {
        var settings = new McpServerSettings { Port = 99999 };
        settings.Validate();
        Assert.Equal(McpServerSettings.DefaultPort, settings.Port);
    }

    [Fact]
    public void Validate_ResetsBlankHost()
    {
        var settings = new McpServerSettings { BindHost = "  " };
        settings.Validate();
        Assert.Equal(McpServerSettings.DefaultBindHost, settings.BindHost);
    }

    [Theory]
    [InlineData("mcp", "/mcp")]
    [InlineData("/mcp/", "/mcp")]
    [InlineData("", "/mcp")]
    [InlineData("  ", "/mcp")]
    public void Validate_NormalizesRoute(string input, string expected)
    {
        var settings = new McpServerSettings { Route = input };
        settings.Validate();
        Assert.Equal(expected, settings.Route);
    }

    [Fact]
    public void Validate_FallsBackToLoopback_WhenWideBindWithoutAuth()
    {
        var settings = new McpServerSettings
        {
            BindHost = "0.0.0.0",
            RequireAuthToken = false,
            AllowNonLoopback = true,
        };
        settings.Validate();
        Assert.Equal(McpServerSettings.DefaultBindHost, settings.BindHost);
    }

    [Fact]
    public void Validate_FallsBackToLoopback_WhenWideBindWithoutExplicitAllow()
    {
        var settings = new McpServerSettings
        {
            BindHost = "0.0.0.0",
            RequireAuthToken = true,
            AllowNonLoopback = false,
        };
        settings.Validate();
        Assert.Equal(McpServerSettings.DefaultBindHost, settings.BindHost);
    }

    [Fact]
    public void Validate_KeepsWideBind_WhenAuthAndExplicitAllow()
    {
        var settings = new McpServerSettings
        {
            BindHost = "0.0.0.0",
            RequireAuthToken = true,
            AllowNonLoopback = true,
        };
        settings.Validate();
        Assert.Equal("0.0.0.0", settings.BindHost);
    }

    [Fact]
    public void AuthDefaults_RequireToken()
    {
        var settings = new McpServerSettings();
        Assert.True(settings.RequireAuthToken);
        Assert.Equal(McpServerSettings.DefaultAuthTokenEnvVar, settings.AuthTokenEnvVar);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("192.168.1.2", false)]
    public void IsLoopback_ClassifiesHosts(string host, bool expected)
    {
        Assert.Equal(expected, McpServerSettings.IsLoopback(host));
    }
}
