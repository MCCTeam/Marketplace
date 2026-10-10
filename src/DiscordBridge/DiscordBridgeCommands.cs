// DiscordBridgeCommands - the dscbridge command: live status, the channel table, per-channel settings, owner DMs, and the retired direction branch, which fails with a pointer instead of going silent.

using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Umpk.Commands;

namespace DMCBK.OfficialPlugins.DiscordBridge;

/// <summary>The <c>dscbridge</c> command: inspect and drive the bridge without touching settings.toml.</summary>
internal sealed class DiscordBridgeCommand(DiscordBridgePlugin plugin, IPluginLocalization strings) : CommandBase
{
    private readonly DiscordBridgePlugin _plugin = plugin;
    private readonly IPluginLocalization _strings = strings;

    /// <inheritdoc/>
    public override string CmdName => "dscbridge";

    /// <inheritdoc/>
    public override string CmdDesc => _strings.Get("command_desc");

    /// <inheritdoc/>
    public override string CmdUsage => _strings.Get("command_usage");

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Plugins;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("status", _strings.Get("usage_status")),
        new("channels", _strings.Get("usage_channels")),
        new("channel <add|remove|set|owners|prefix|test> ...", _strings.Get("usage_channel")),
        new("dm <on|off|messages <on|off>|<owner> <text>>", _strings.Get("usage_dm")),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples =>
    [
        "dscbridge status",
        "dscbridge channel add general 123 456",
        "dscbridge channel set general cmd on",
        "dscbridge dm on",
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["console-chat"];

    /// <inheritdoc/>
    public override string? ManTopic => "discord-bridge";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        builder.Literal(CmdName, l => l
            .ThenLiteral("status", h => h
                .Executes(ctx => _plugin.ShowStatus(ctx.Source)))
            .ThenLiteral("channels", h => h
                .Executes(ctx => _plugin.ListChannels(ctx.Source)))
            .ThenLiteral("channel", h => h
                .ThenLiteral("add", a => a
                    .ThenArgument("name", Arguments.QuotableString(), b => b
                        .ThenArgument("guild", Arguments.QuotableString(), c => c
                            .ThenArgument("channel", Arguments.QuotableString(), d => d
                                .Executes(ctx => _plugin.ChannelAdd(
                                    ctx.Source,
                                    ctx.GetArgument<string>("name"),
                                    ctx.GetArgument<string>("guild"),
                                    ctx.GetArgument<string>("channel")))))))
                .ThenLiteral("remove", a => a
                    .ThenArgument("name", Arguments.QuotableString(), b => b
                        .Executes(ctx => _plugin.ChannelRemove(ctx.Source, ctx.GetArgument<string>("name")))))
                .ThenLiteral("set", a => a
                    .ThenArgument("name", Arguments.QuotableString(), b => b
                        .ThenArgument("flag", Arguments.QuotableString(), c => c
                            .ThenArgument("value", Arguments.QuotableString(), d => d
                                .Executes(ctx => _plugin.ChannelSetFlag(
                                    ctx.Source,
                                    ctx.GetArgument<string>("name"),
                                    ctx.GetArgument<string>("flag"),
                                    ctx.GetArgument<string>("value")))))))
                .ThenLiteral("owners", a => a
                    .ThenArgument("name", Arguments.QuotableString(), b => b
                        .ThenLiteral("inherit", c => c
                            .Executes(ctx => _plugin.ChannelSetOwners(
                                ctx.Source, ctx.GetArgument<string>("name"), string.Empty, inherit: true)))
                        .ThenArgument("ids", Arguments.GreedyString(), c => c
                            .Executes(ctx => _plugin.ChannelSetOwners(
                                ctx.Source,
                                ctx.GetArgument<string>("name"),
                                ctx.GetArgument<string>("ids"),
                                inherit: false)))))
                .ThenLiteral("prefix", a => a
                    .ThenArgument("name", Arguments.QuotableString(), b => b
                        .ThenLiteral("default", c => c
                            .Executes(ctx => _plugin.ChannelSetPrefix(
                                ctx.Source, ctx.GetArgument<string>("name"), string.Empty, useDefault: true)))
                        .ThenArgument("char", Arguments.QuotableString(), c => c
                            .Executes(ctx => _plugin.ChannelSetPrefix(
                                ctx.Source,
                                ctx.GetArgument<string>("name"),
                                ctx.GetArgument<string>("char"),
                                useDefault: false)))))
                .ThenLiteral("test", a => a
                    .ThenArgument("name", Arguments.QuotableString(), b => b
                        .Executes(ctx => _plugin.ChannelTest(ctx.Source, ctx.GetArgument<string>("name"))))))
            .ThenLiteral("dm", h => h
                .ThenLiteral("on", a => a
                    .Executes(ctx => _plugin.SetDirectMessages(ctx.Source, true)))
                .ThenLiteral("off", a => a
                    .Executes(ctx => _plugin.SetDirectMessages(ctx.Source, false)))
                .ThenLiteral("messages", m => m
                    .ThenLiteral("on", a => a
                        .Executes(ctx => _plugin.SetDmMessages(ctx.Source, true)))
                    .ThenLiteral("off", a => a
                        .Executes(ctx => _plugin.SetDmMessages(ctx.Source, false))))
                .ThenArgument("owner", Arguments.QuotableString(), a => a
                    .ThenArgument("text", Arguments.GreedyString(), b => b
                        .Executes(ctx => _plugin.SendDirectMessage(
                            ctx.Source,
                            ctx.GetArgument<string>("owner"),
                            ctx.GetArgument<string>("text"))))))
            .ThenLiteral("direction", h => h
                .Executes(ctx => ctx.Source.Result.Fail(_strings.Get("direction_retired"))))
            .ThenLiteral("_help", h => h
                .Executes(ctx => ShowUsage(ctx.Source))
                .RedirectTo(help)));
    }
}
