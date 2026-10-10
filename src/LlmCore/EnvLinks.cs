namespace DMCBK.OfficialPlugins.LlmCore;

/// <summary>Resolves "env:NAME" links the way the bridge plugins do. Unset or empty reads as blank.</summary>
public static class EnvLinks
{
    public const string Prefix = "env:";

    public static string? NameOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string text = value.Trim();
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string name = text[Prefix.Length..].Trim();
        return name.Length == 0 ? null : name;
    }

    public static string Resolve(string? value)
    {
        string? name = NameOf(value);
        if (name is null)
            return (value ?? string.Empty).Trim();

        return (Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();
    }

    /// <summary>Reads a secret by variable name. Empty when unset, so callers fail with a named message.</summary>
    public static string Secret(string? variable)
    {
        if (string.IsNullOrWhiteSpace(variable))
            return string.Empty;

        return (Environment.GetEnvironmentVariable(variable.Trim()) ?? string.Empty).Trim();
    }
}
