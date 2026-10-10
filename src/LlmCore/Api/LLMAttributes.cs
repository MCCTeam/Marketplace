namespace LlmCore.Api;

/// <summary>Method-level tool declaration. The core validates the signature at registration time.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LLMToolAttribute(string name, string description) : Attribute
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string JsonSchema { get; set; } = """{"type":"object","properties":{}}""";
    public bool NeedsApproval { get; set; } = true;
}

/// <summary>Class-level single skill folder. The path resolves against the registration base path.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class LLMSkillAttribute(string name, string path) : Attribute
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public string? Description { get; set; }
    public string? Owner { get; set; }
    public string? Version { get; set; }
    public string[]? Keywords { get; set; }
}

/// <summary>Class-level skill tree root. Every nested skill folder underneath registers.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class LLMSkillsAttribute(string path) : Attribute
{
    public string Path { get; } = path;
}
