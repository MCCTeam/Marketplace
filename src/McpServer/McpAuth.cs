using System.Security.Cryptography;

namespace DMCBK.OfficialPlugins.McpServer;

/// <summary>
/// Bearer-token validation for the MCP endpoint.
/// Pure and unit-testable: the ASP.NET middleware is a thin adapter over <see cref="IsAuthorized"/>, so the truth table (missing header, wrong scheme, wrong token, route-prefix scoping) is pinned without binding a socket.
/// </summary>
public static class McpAuth
{
    /// <summary>Minimum accepted token length in characters. Below this the token is refused as too weak.</summary>
    public const int MinTokenLength = 32;

    /// <summary>Resolves the required token, or null when auth cannot be satisfied.</summary>
    /// <returns>The token, or null with <paramref name="error"/> set to missing/weak.</returns>
    public static string? ResolveRequiredToken(string envVar, out string? error)
    {
        error = null;
        string? token = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(token))
        {
            error = McpErrorCodes.MissingAuthToken;
            return null;
        }

        if (token.Length < MinTokenLength)
        {
            error = McpErrorCodes.WeakAuthToken;
            return null;
        }

        return token;
    }

    /// <summary>
    /// True when the request path needs auth and the header carries the required bearer token.
    /// Paths outside <paramref name="route"/> are not the MCP endpoint and are left alone.
    /// </summary>
    public static bool IsAuthorized(string requestPath, string? authorizationHeader, string route, string requiredToken)
    {
        ArgumentNullException.ThrowIfNull(requestPath);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(requiredToken);

        if (!IsUnderRoute(requestPath, route))
            return true;

        if (string.IsNullOrEmpty(authorizationHeader)
            || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;

        string presented = authorizationHeader["Bearer ".Length..].Trim();
        return FixedTimeEquals(presented, requiredToken);
    }

    /// <summary>True when the path is the route or nested under it, case-insensitively.</summary>
    public static bool IsUnderRoute(string requestPath, string route)
    {
        if (requestPath.Equals(route, StringComparison.OrdinalIgnoreCase))
            return true;

        return requestPath.StartsWith(route.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool FixedTimeEquals(string presented, string required)
    {
        byte[] a = System.Text.Encoding.UTF8.GetBytes(presented);
        byte[] b = System.Text.Encoding.UTF8.GetBytes(required);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
