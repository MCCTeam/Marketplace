using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace DMCBK.OfficialPlugins.Tests;

/// <summary>
/// The discipline AGENTS.md already demands of <c>Mcc/</c>, applied to the plugins that have been converted to <c>lang/</c> files: nothing a user reads may be a literal in the source.
/// <para>
/// The list is EXPLICIT and grows as plugins are converted.
/// Failing for every unconverted plugin on day one would have made the test a permanent red mark nobody could act on; failing for a converted one is a real regression, because the plugin already has the file the string belongs in.
/// </para>
/// </summary>
public sealed partial class PluginStringDisciplineTests
{
    /// <summary>
    /// The plugins whose text has been moved into <c>lang/en.toml</c>.
    /// Add a plugin here in the same commit that converts it.
    /// </summary>
    private static readonly string[] ConvertedPlugins =
    [
        "AutoEat", "Alerts",
        "PlayerListLogger", "RemoteControl", "ItemsCollector", "ChatLog", "AntiAFK",
        "AutoRelog", "AutoDrop", "ScriptScheduler", "AutoAttack", "AutoRespond",
        "AutoCraft", "AutoDig", "AutoFishing", "DiscordBridge", "DiscordRpc", "Farmer",
        "FollowPlayer", "Map", "ReplayCapture", "TelegramBridge",
    ];

    /// <summary>
    /// The calls whose arguments are text a user reads.
    /// <c>Log*</c> covers the whole <c>Microsoft.Extensions.Logging</c> family and any local <c>Log</c> helper wrapping it; the rest are the host seams a plugin reaches a person through.
    /// </summary>
    private static readonly string[] WatchedMethods = ["TryNotify", "Reply", "Ok", "Fail", "Notify"];

    public static TheoryData<string> Plugins()
    {
        var data = new TheoryData<string>();
        foreach (string plugin in ConvertedPlugins)
            data.Add(plugin);

        return data;
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void AConvertedPlugin_HasNoUserFacingLiteral(string plugin)
    {
        string folder = Path.Combine(RepositoryRoot(), "src", plugin);
        Assert.True(Directory.Exists(folder), $"No plugin folder at '{folder}'.");

        IReadOnlyList<string> offences = Scan(folder);

        Assert.True(
            offences.Count == 0,
            $"Plugin '{plugin}' is converted to lang/ files, so no user-facing string may be a literal in its"
            + " source. Move these into lang/en.toml and read them through context.Strings:\n  "
            + string.Join("\n  ", offences));
    }

    /// <summary>
    /// The English table has to exist for the discipline to mean anything: keys read through <c>context.Strings</c> fall back to English, and a converted plugin with no English file would show every one of them as its own key.
    /// </summary>
    [Theory]
    [MemberData(nameof(Plugins))]
    public void AConvertedPlugin_ShipsAnEnglishTable(string plugin)
    {
        string path = Path.Combine(RepositoryRoot(), "src", plugin, "lang", "en.toml");
        Assert.True(File.Exists(path), $"Plugin '{plugin}' is on the converted list but has no '{path}'.");
    }

    /// <summary>
    /// The scanner's own proof, because a scanner that finds nothing looks exactly like a scanner that cannot find anything.
    /// This is the shape Alerts actually had before it was converted: prose built into a local, then handed to the logger.
    /// Checking only the call's first argument, which is what a naive reading of the rule gives, would have missed it.
    /// </summary>
    [Fact]
    public void TheScanner_CatchesProseThatReachesTheCallThroughALocal()
    {
        const string Source = """
            public sealed class Sample
            {
                private void Notify(object context, string raw)
                {
                    string line = $"[Alert] {raw}";
                    Logger.LogInformation("{Message}", line);
                }
            }
            """;

        Assert.Contains(
            Scan(Source, "Sample.cs"),
            offence => offence.Contains("[Alert]", System.StringComparison.Ordinal));
    }

    /// <summary>A translation key is not prose, and neither is the structured-logging template.</summary>
    [Fact]
    public void TheScanner_AcceptsKeysAndPlaceholderTemplates()
    {
        const string Source = """
            public sealed class Sample
            {
                private void Run(object context)
                {
                    Log(context, "session.ended");
                    Logger.LogInformation("{Message}", context.Strings.Format("chat.said", a, b));
                }
            }
            """;

        Assert.Empty(Scan(Source, "Sample.cs"));
    }

    private static IReadOnlyList<string> Scan(string folder)
    {
        var offences = new List<string>();
        foreach (string path in Directory.EnumerateFiles(folder, "*.cs", SearchOption.TopDirectoryOnly))
            offences.AddRange(Scan(File.ReadAllText(path), Path.GetFileName(path)));

        return offences;
    }

    private static List<string> Scan(string source, string fileName)
    {
        var offences = new List<string>();
        SyntaxNode root = CSharpSyntaxTree.ParseText(source).GetRoot();

        foreach (InvocationExpressionSyntax call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (NameOf(call) is not { } name || !IsWatched(name))
                continue;

            foreach (ArgumentSyntax argument in call.ArgumentList.Arguments)
            {
                // One hop of constant propagation: prose is usually built into a local and then passed, so an identifier is followed to the local's initializer before being judged.
                ExpressionSyntax expression = Resolve(argument.Expression, call);
                if (!IsProse(expression, out string? text))
                    continue;

                FileLinePositionSpan span = call.SyntaxTree.GetLineSpan(call.Span);
                offences.Add($"{fileName}:{span.StartLinePosition.Line + 1}: {name}(... \"{text}\" ...)");
            }
        }

        return offences;
    }

    private static bool IsWatched(string name)
        => name.StartsWith("Log", StringComparison.Ordinal)
            || Array.IndexOf(WatchedMethods, name) >= 0;

    /// <summary>The invoked member's simple name, or null for a shape this scanner does not read.</summary>
    private static string? NameOf(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>
    /// Follows a plain identifier to the single-assignment local it names, within the member the call sits in.
    /// Anything else is returned unchanged, so the caller simply judges what it was given.
    /// </summary>
    private static ExpressionSyntax Resolve(ExpressionSyntax expression, SyntaxNode call)
    {
        if (expression is not IdentifierNameSyntax identifier)
            return expression;

        SyntaxNode? scope = call.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        if (scope is null)
            return expression;

        string name = identifier.Identifier.ValueText;
        foreach (VariableDeclaratorSyntax declarator in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Identifier.ValueText == name && declarator.Initializer is { } initializer)
                return initializer.Value;
        }

        return expression;
    }

    /// <summary>
    /// Whether an expression is prose rather than plumbing.
    /// The test is WHITESPACE: a sentence has some, a translation key (<c>session.ended</c>), a file name (<c>alerts-log.txt</c>) and a structured-logging template (<c>{Message}</c>) do not.
    /// <para>
    /// It follows that a genuinely user-facing single word slips through.
    /// That is the deliberate trade: the alternative is a scanner that flags every identifier-shaped literal in the file and gets switched off.
    /// Interpolated strings are judged on their literal parts, and deliberately so, because
    /// <c>$"[Alert] {raw}"</c> is exactly the hardcoding this test exists to catch.
    /// </para>
    /// </summary>
    private static bool IsProse(ExpressionSyntax expression, out string? text)
    {
        text = null;
        string value = expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression)
                => literal.Token.ValueText,
            InterpolatedStringExpressionSyntax interpolated => string.Concat(
                interpolated.Contents.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText)),
            _ => string.Empty,
        };

        if (value.Length == 0 || !value.Any(char.IsWhiteSpace))
            return false;

        // A template made only of placeholders carries no words.
        // It is how already-localized text is passed through a logger without the text itself being read as a template, and it is what this repository writes everywhere: LogInformation("{Message}", alreadyTranslated).
        if (PlaceholdersOnly().Replace(value, string.Empty).Trim().Length == 0)
            return false;

        text = value.Length <= 48 ? value : value[..45] + "...";
        return true;
    }

    /// <summary>
    /// The repository root, found by walking up from the test assembly until <c>plugins/</c> appears.
    /// The test reads the plugins' SOURCE, not their compiled output, so it needs the tree and not the bin folder.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                && File.Exists(Path.Combine(directory.FullName, "Marketplace.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex PlaceholdersOnly();
}
