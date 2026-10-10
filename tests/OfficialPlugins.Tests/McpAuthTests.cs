using DMCBK.OfficialPlugins.McpServer;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>The bearer truth table, pinned without binding a socket.</summary>
public sealed class McpAuthTests
{
    private const string Var = "MCC_MCP_AUTH_TEST_TOKEN";

    [Fact]
    public void Resolve_MissingToken_Fails()
    {
        Environment.SetEnvironmentVariable(Var, null);
        string? token = McpAuth.ResolveRequiredToken(Var, out string? error);
        Assert.Null(token);
        Assert.Equal(McpErrorCodes.MissingAuthToken, error);
    }

    [Fact]
    public void Resolve_WeakToken_Fails()
    {
        Environment.SetEnvironmentVariable(Var, "short");
        try
        {
            string? token = McpAuth.ResolveRequiredToken(Var, out string? error);
            Assert.Null(token);
            Assert.Equal(McpErrorCodes.WeakAuthToken, error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Var, null);
        }
    }

    [Fact]
    public void Resolve_StrongToken_Passes()
    {
        Environment.SetEnvironmentVariable(Var, new string('a', 32));
        try
        {
            string? token = McpAuth.ResolveRequiredToken(Var, out string? error);
            Assert.Equal(new string('a', 32), token);
            Assert.Null(error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Var, null);
        }
    }

    [Theory]
    [InlineData("/mcp", true)]
    [InlineData("/mcp/sse", true)]
    [InlineData("/MCP", true)]
    [InlineData("/other", false)]
    [InlineData("/mcp2", false)]
    public void IsUnderRoute_ScopesCorrectly(string path, bool expected)
    {
        Assert.Equal(expected, McpAuth.IsUnderRoute(path, "/mcp"));
    }

    [Fact]
    public void IsAuthorized_AcceptsBearerCaseInsensitively()
    {
        string token = new string('t', 32);
        Assert.True(McpAuth.IsAuthorized("/mcp", "Bearer " + token, "/mcp", token));
        Assert.True(McpAuth.IsAuthorized("/mcp", "bearer " + token, "/mcp", token));
        Assert.False(McpAuth.IsAuthorized("/mcp", "Bearer wrong", "/mcp", token));
        Assert.False(McpAuth.IsAuthorized("/mcp", "Token " + token, "/mcp", token));
        Assert.False(McpAuth.IsAuthorized("/mcp", null, "/mcp", token));
    }

    [Fact]
    public void IsAuthorized_LeavesNonRoutePathsAlone()
    {
        Assert.True(McpAuth.IsAuthorized("/health", null, "/mcp", new string('t', 32)));
    }
}
