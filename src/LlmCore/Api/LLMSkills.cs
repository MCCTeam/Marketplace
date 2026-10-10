namespace LlmCore.Api;

/// <summary>Registers one skill folder by code. Directory bounds every file read, or is null for bare text.</summary>
public sealed record LLMSkillDefinition(
    string Name,
    string Description,
    string? Directory,
    string SkillFile,
    string? Owner = null,
    string? Version = null,
    IReadOnlyList<string>? Keywords = null);

/// <summary>What listings hand back. Owner is absent when the registering plugin never set one.</summary>
public sealed record LLMSkillSummary(
    string Name,
    string Description,
    string? Owner = null,
    string? Version = null,
    IReadOnlyList<string>? Keywords = null);
