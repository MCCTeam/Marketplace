using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DMCBK.Core.Commands;
using DMCBK.OfficialPlugins.Alerts;
using DMCBK.Testing;
using Umpk;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Packets;
using Umpk.Text;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The Alerts plugin, loaded for real through PluginTestHost and driven with fake-server packets: keyword and regex rules, excludes, event rules with cooldowns, the judge against a loopback stub LLM, batching, route modes, and every command.
/// No key, no network beyond localhost.
/// </summary>
public sealed class AlertsTests : IDisposable
{
    /// <summary>
    /// The full suite parks pool threads all over (blocking command dispatches, process waits) while fake-server sessions need prompt pump threads on few cores.
    /// A higher floor keeps one scheduling cliff from reading as a plugin defect.
    /// Preloading the LLM SDK pair also keeps every plugin load context coherent: llm-core must resolve OpenAI and System.ClientModel from the SAME context, and a partial default (one present, one not) splits the closure and breaks SDK constructors mid-run.
    /// </summary>
    [ModuleInitializer]
    internal static void RaiseThreadPoolFloor()
    {
        ThreadPool.GetMinThreads(out int workers, out int ports);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), Math.Max(ports, 16));
#pragma warning disable OPENAI001 // Test-only preload; see the comment above.
        GC.KeepAlive(typeof(OpenAI.Responses.ResponsesClient));
#pragma warning restore OPENAI001
        GC.KeepAlive(typeof(System.ClientModel.ApiKeyCredential));
    }

    private readonly StubLlm _stub = new();
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        _stub.Dispose();
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void VerdictParser_Accepts_Rejects_And_Batches()
    {
        LlmVerdict? yes = VerdictParser.TryParse(
            """{"alert":true,"confidence":0.9,"reason":"griefing","routes":["discord"]}""", null);
        Assert.NotNull(yes);
        Assert.True(yes.Alert);
        Assert.Equal(0.9, yes.Confidence);
        Assert.Equal(["discord"], yes.Routes);

        Assert.Null(VerdictParser.TryParse("not json", null));
        Assert.Null(VerdictParser.TryParse(null, null));
        Assert.Null(VerdictParser.TryParse("""{"confidence":0.5}""", null));

        // Text fallback when the core hands back unstructured text.
        LlmVerdict? fromText = VerdictParser.TryParse(null, """{"alert":false,"confidence":0.2}""");
        Assert.NotNull(fromText);
        Assert.False(fromText.Alert);

        List<LlmVerdict?> batch = VerdictParser.TryParseBatch(
            """[{"alert":true,"confidence":0.8},{"alert":false,"confidence":0.9}]""", null, 2);
        Assert.Equal(2, batch.Count);
        Assert.True(batch[0]!.Alert);
        Assert.False(batch[1]!.Alert);
    }

    [Fact]
    public void Routes_Normalize_And_Default_To_All()
    {
        Assert.Equal(["desktop", "file"], AlertRoutes.Normalize(["file", "desktop", "desktop", "nope"]));
        Assert.Empty(AlertRoutes.Normalize(null));
        Assert.True(AlertRoutes.IsKnown("Discord"));
        Assert.False(AlertRoutes.IsKnown("carrier-pigeon"));
        Assert.Equal(5, AlertsPlugin.EffectiveRoutes(null).Count);
        Assert.Equal(5, AlertsPlugin.EffectiveRoutes([]).Count);
        Assert.Equal(["telegram"], AlertsPlugin.EffectiveRoutes(["telegram"]));
    }

    [Fact]
    public void DesktopNotifier_Builds_Os_Command()
    {
        if (OperatingSystem.IsLinux())
        {
            var start = DesktopNotifier.BuildToast("Hello", "body text");
            Assert.NotNull(start);
            Assert.Contains("notify-send", start.FileName);
            Assert.Contains("Hello", start.Arguments);
        }

        string script = DesktopNotifier.BuildWindowsScript("Hi <there>", "line one");
        Assert.Contains("ToastNotificationManager", script);
        Assert.Contains("Hi &lt;there&gt;", script);
    }

    [Fact]
    public void Settings_Validate_Clamps_And_Migrates_Legacy()
    {
        var settings = new AlertsSettings
        {
            Matches = ["admin"],
            Beep = false,
            Smart = new AlertsSmartConfig { Mode = "bogus", Confidence = 99 },
        };
        settings.Validate();
        Assert.Single(settings.Rule);
        Assert.Equal("legacy", settings.Rule[0].Name);
        Assert.False(settings.Rule[0].Beep);
        Assert.Equal("off", settings.Smart.Mode);
        Assert.Equal(1.0, settings.Smart.Confidence);
    }

    [Fact]
    public async Task KeywordRule_Fires_File_Route_And_Excludes_Win()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        AddAlerts(host, """
            Enabled = true
            LogToFile = true
            Excludes = [ "joined the game" ]
            [[Rule]]
            Name = "admin-watch"
            Match = [ "admin" ]
            Routes = [ "file" ]
            Beep = false
            """);
        await host.LoadAsync();

        string log = host.DataFile("alerts", "alerts-log.txt");
        await host.RunSessionAsync(async session =>
        {
            await session.SendAsync(
                new ClientboundSystemChatPacket(Component.Text("<Alice> admin online"), false), ct: TestToken());
            Assert.True(await host.WaitForAsync(() => Read(log).Contains("admin online", StringComparison.Ordinal)));

            await session.SendAsync(
                new ClientboundSystemChatPacket(Component.Text("<Bob> hello there"), false), ct: TestToken());
            await session.SendAsync(
                new ClientboundSystemChatPacket(Component.Text("admin joined the game"), false), ct: TestToken());
            await Task.Delay(500, TestToken());
            Assert.DoesNotContain("hello there", Read(log), StringComparison.Ordinal);
            Assert.DoesNotContain("joined the game", Read(log), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RegexRule_Matches_Patterns()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        AddAlerts(host, """
            Enabled = true
            LogToFile = true
            [[Rule]]
            Name = "grief"
            Match = [ "gr[i1]ef" ]
            Routes = [ "file" ]
            Beep = false
            Regex = true
            """);
        await host.LoadAsync();

        string log = host.DataFile("alerts", "alerts-log.txt");
        await host.RunSessionAsync(async session =>
        {
            await session.SendAsync(
                new ClientboundSystemChatPacket(Component.Text("<Steve> stop gr1efing"), false), ct: TestToken());
            Assert.True(await host.WaitForAsync(() => Read(log).Contains("gr1efing", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public async Task EventRules_Fire_Death_Explosion_And_Cooldown()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        AddAlerts(host, """
            Enabled = true
            LogToFile = true
            [[EventRule]]
            Name = "death"
            On = [ "died" ]
            Routes = [ "file" ]
            Beep = false
            [[EventRule]]
            Name = "booms"
            On = [ "explosion" ]
            Routes = [ "file" ]
            Beep = false
            CooldownSeconds = 60
            """);
        await host.LoadAsync();

        string log = host.DataFile("alerts", "alerts-log.txt");
        await host.RunSessionAsync(async session =>
        {
            CmdResult events = await Dispatch(host, "alerts events");
            Assert.Contains("death", events.Message, StringComparison.Ordinal);

            var boom = new ClientboundExplodePacket(
                new Umpk.Geometry.Vec3d(10, 64, 10), 3, [], 0, 0, 0, null,
                new Umpk.Protocol.Java.Codecs.ParticleData(22, []),
                new Umpk.Protocol.Java.Packets.SoundEventHolder(0, null, null), 3, 0, []);
            await session.SendAsync(
                new ClientboundLoginPacket(
                    1, false, ["minecraft:overworld"], 20, 8, 8, false, true, false,
                    new CommonPlayerSpawnInfo(0, "minecraft:overworld", 0, 1, -1, false, false, null, 0, 63),
                    false, false, null),
                ct: TestToken());
            await session.SendAsync(boom, ct: TestToken());
            await session.SendAsync(boom, ct: TestToken());
            await session.SendAsync(
                new ClientboundSystemChatPacket(Component.Text("<Alice> still alive"), false), ct: TestToken());
            Assert.True(await host.WaitForAsync(() => host.Client.Status.ToString() == "Playing"),
                "Client left Playing state after the explosion packets.");
            Assert.True(await host.WaitForAsync(() => Read(log).Contains("Explosion", StringComparison.Ordinal)));
            await Task.Delay(500, TestToken());
            Assert.Equal(1, Read(log).Split("Explosion", StringSplitOptions.None).Length - 1);

            await session.SendAsync(new ClientboundSetHealthPacket(0, 20, 5), ct: TestToken());
            Assert.True(await host.WaitForAsync(() => Read(log).Contains("died", StringComparison.Ordinal)),
                "Client died after the explosion packets, so the packet broke the session.");
        });
    }

    [Fact]
    public async Task SmartGated_Confirms_And_Rejects_Via_Stub()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        AddAlerts(host, SmartSettings(mode: "rules"));
        string llm = AddLlmCore(host);
        await host.LoadAsync();

        string log = host.DataFile("alerts", "alerts-log.txt");
        await RunLongSessionAsync(host, async session =>
        {
            await Dispatch(host, $"llm provider add stub {_stub.Url}");
            await Dispatch(host, "llm use stub/stub-model");

            await session.SendAsync(
                new ClientboundSystemChatPacket(Component.Text("<Alice> admin ALERT-YES now"), false), ct: TestToken());
            Assert.True(await WaitForJudgeAsync(host, session, () => Read(log).Contains("ALERT-YES", StringComparison.Ordinal)));

            await session.SendAsync(
                new ClientboundSystemChatPacket(Component.Text("<Bob> admin boring meeting"), false), ct: TestToken());
            bool twice = await WaitForJudgeAsync(host, session, () =>
                StatusText(host).Contains("calls: 2", StringComparison.Ordinal));
            if (!twice)
            {
                Assert.Fail("Second judge call never finished. Client=" + host.Client.Status
                    + " Status: " + StatusText(host));
            }
            await Task.Delay(500, TestToken());
            Assert.DoesNotContain("boring meeting", Read(log), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task SmartFull_Judges_Unmatched_Lines()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        AddAlerts(host, SmartSettings(mode: "full"));
        AddLlmCore(host);
        await host.LoadAsync();

        string log = host.DataFile("alerts", "alerts-log.txt");
        await RunLongSessionAsync(host, async session =>
        {
            await Dispatch(host, $"llm provider add stub {_stub.Url}");
            await Dispatch(host, "llm use stub/stub-model");

            await session.SendAsync(
                new ClientboundSystemChatPacket(Component.Text("<Steve> someone ALERT-YES griefed my house"), false),
                ct: TestToken());
            Assert.True(await WaitForJudgeAsync(host, session, () => Read(log).Contains("ALERT-YES", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public async Task Batching_Groups_Low_Priority_Lines()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        AddAlerts(host, SmartSettings(mode: "full", batch: true));
        AddLlmCore(host);
        await host.LoadAsync();

        string log = host.DataFile("alerts", "alerts-log.txt");
        await RunLongSessionAsync(host, async session =>
        {
            await Dispatch(host, $"llm provider add stub {_stub.Url}");
            await Dispatch(host, "llm use stub/stub-model");
            int before = _stub.RequestCount;

            await session.SendAsync(
                new ClientboundSystemChatPacket(Component.Text("<A> first ALERT-YES batch line"), false), ct: TestToken());
            await session.SendAsync(
                new ClientboundSystemChatPacket(Component.Text("<B> second ALERT-YES batch line"), false), ct: TestToken());
            Assert.True(await WaitForJudgeAsync(host, session, () => Read(log).Contains("second ALERT-YES", StringComparison.Ordinal)));
            // Two lines, one judge call: the batch grouped them.
            Assert.Equal(before + 1, _stub.RequestCount);
        });
    }

    [Fact]
    public async Task RouteMode_Model_Lets_Judge_Pick_Subset()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        AddAlerts(host, SmartSettings(mode: "rules", routeMode: "model"));
        AddLlmCore(host);
        await host.LoadAsync();

        string log = host.DataFile("alerts", "alerts-log.txt");
        await RunLongSessionAsync(host, async session =>
        {
            await Dispatch(host, $"llm provider add stub {_stub.Url}");
            await Dispatch(host, "llm use stub/stub-model");

            // The stub answers routes:["desktop"] here, so the file route stays silent.
            await session.SendAsync(
                new ClientboundSystemChatPacket(Component.Text("<Alice> admin ONLY-DESKTOP ALERT-YES"), false),
                ct: TestToken());
            bool called = await WaitForJudgeAsync(host, session, () => StatusText(host).Contains("calls: 1", StringComparison.Ordinal));
            if (!called)
                Assert.Fail("Judge call never finished. Client=" + host.Client.Status + " Status: " + StatusText(host));
            CmdResult smart = await Dispatch(host, "alerts smart status");
            Assert.Contains("errors: 0", smart.Message, StringComparison.Ordinal);
            await Task.Delay(500, TestToken());
            Assert.DoesNotContain("ONLY-DESKTOP", Read(log), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MissingCore_Fires_Directly()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        AddAlerts(host, SmartSettings(mode: "rules"));
        await host.LoadAsync();

        string log = host.DataFile("alerts", "alerts-log.txt");
        await host.RunSessionAsync(async session =>
        {
            await session.SendAsync(
                new ClientboundSystemChatPacket(Component.Text("<Alice> admin here"), false), ct: TestToken());
            Assert.True(await host.WaitForAsync(() => Read(log).Contains("admin here", StringComparison.Ordinal)));

            CmdResult status = await Dispatch(host, "alerts smart status");
            Assert.Contains("missing", status.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task BadModel_Reports_Error_And_Fires_Directly()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        string folder = OfficialPluginFixture.Add(host, Path.Combine(RepositoryRoot(), "src", "Alerts"));
        File.WriteAllText(Path.Combine(folder, "settings.toml"), """
            Enabled = true
            LogToFile = true
            RouteMode = "fixed"
            [[Rule]]
            Name = "admin-watch"
            Match = [ "admin" ]
            Routes = [ "desktop", "file" ]
            Beep = false
            Smart = true
            [[EventRule]]
            Name = "death"
            On = [ "died" ]
            Routes = [ "file" ]
            Beep = false
            [Smart]
            Mode = "rules"
            Model = "nosuchprovider/whatever"
            Confidence = 0.7
            Prompt = "Alert when the line needs a human. Ignore the rest."
            Batch = false
            BatchSize = 10
            BatchSeconds = 1
            Context = [ ]
            """);
        AddLlmCore(host);
        await host.LoadAsync();

        string log = host.DataFile("alerts", "alerts-log.txt");
        await host.RunSessionAsync(async session =>
        {
            await Dispatch(host, $"llm provider add stub {_stub.Url}");

            await session.SendAsync(
                new ClientboundSystemChatPacket(Component.Text("<Alice> admin here"), false), ct: TestToken());
            // Gated failure still alerts: the keyword already matched.
            Assert.True(await host.WaitForAsync(() => Read(log).Contains("admin here", StringComparison.Ordinal)));

            CmdResult status = await Dispatch(host, "alerts smart status");
            Assert.Contains("errors: 1", status.Message, StringComparison.Ordinal);
            Assert.Contains("Last error", status.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Commands_List_Rules_Events_And_Test()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        AddAlerts(host, SmartSettings(mode: "rules"));
        AddLlmCore(host);
        await host.LoadAsync();

        await RunLongSessionAsync(host, async session =>
        {
            await Dispatch(host, $"llm provider add stub {_stub.Url}");
            await Dispatch(host, "llm use stub/stub-model");

            CmdResult rules = await Dispatch(host, "alerts rules");
            Assert.Contains("admin-watch", rules.Message, StringComparison.Ordinal);
            Assert.Contains("[smart]", rules.Message, StringComparison.Ordinal);

            CmdResult events = await Dispatch(host, "alerts events");
            Assert.Contains("death", events.Message, StringComparison.Ordinal);

            CmdResult routes = await Dispatch(host, "alerts routes");
            Assert.Contains("file", routes.Message, StringComparison.Ordinal);

            CmdResult test = await Dispatch(host, "alerts test \"admin ALERT-YES please\"");
            Assert.Contains("alert=True", test.Message, StringComparison.Ordinal);

            CmdResult set = await Dispatch(host, "alerts routes set admin-watch file");
            Assert.Contains("file", set.Message, StringComparison.Ordinal);

            CmdResult bad = await host.Client.Commands.DispatchAsync("alerts routes set admin-watch pigeon");
            Assert.Equal(CmdStatus.Fail, bad.Status);
        });
    }

    private static CancellationToken TestToken() => CancellationToken.None;

    /// <summary>
    /// Runs the session on a 90s token instead of the 15s harness budget.
    /// Judge tests need it: when the box is loaded their background LLM round trip can outlive the default budget, and the client going quiet mid-test looks exactly like a broken plugin.
    /// </summary>
    private static async Task RunLongSessionAsync(PluginTestHost host, Func<PluginTestSession, Task> body)
    {
        using var sessionCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await host.RunSessionAsync(body, sessionCts.Token);
    }

    /// <summary>
    /// Judge round trips (chat delivery plus a background LLM call) get a longer leash than the harness budget: under full-suite parallel load on small machines they can need it.
    /// </summary>
    private static async Task<bool> WaitForJudgeAsync(PluginTestHost host, Func<bool> condition)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await host.WaitForAsync(condition, budget.Token);
    }

    /// <summary>
    /// Same as <see cref="WaitForJudgeAsync"/>, but keeps the fake session alive: the fake server sends nothing on its own, and the client drops itself after 30s without inbound traffic (real servers keep-alive constantly).
    /// A time packet every 10s holds the session open.
    /// </summary>
    private static async Task<bool> WaitForJudgeAsync(
        PluginTestHost host, PluginTestSession session, Func<bool> condition)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var keepalive = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            try
            {
                while (!keepalive.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), keepalive.Token);
                    try
                    {
                        await session.SendAsync(
                            new ClientboundSetTimePacket(0, 0, false, []), ct: keepalive.Token);
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
        try
        {
            return await host.WaitForAsync(condition, budget.Token);
        }
        finally
        {
            keepalive.Cancel();
            await pump;
        }
    }

    private static string SmartSettings(string mode, bool batch = false, string routeMode = "fixed", string model = "stub/stub-model")
        => $"""
        Enabled = true
        LogToFile = true
        RouteMode = "{routeMode}"
        [[Rule]]
        Name = "admin-watch"
        Match = [ "admin" ]
        Routes = [ "desktop", "file" ]
        Beep = false
        Smart = true
        [[EventRule]]
        Name = "death"
        On = [ "died" ]
        Routes = [ "file" ]
        Beep = false
        [Smart]
        Mode = "{mode}"
        Model = "{model}"
        Confidence = 0.7
        Prompt = "Alert when the line needs a human. Ignore the rest."
        CatchAllRoutes = [ "file" ]
        Batch = {(batch ? "true" : "false")}
        BatchSize = 10
        BatchSeconds = 1
        Context = [ ]
        """;

    private void AddAlerts(PluginTestHost host, string settings)
    {
        string folder = OfficialPluginFixture.Add(host, Path.Combine(RepositoryRoot(), "src", "Alerts"));
        File.WriteAllText(Path.Combine(folder, "settings.toml"), settings);
    }

    private string AddLlmCore(PluginTestHost host)
    {
        string folder = OfficialPluginFixture.Add(host, Path.Combine(RepositoryRoot(), "src", "LlmCore"));
        File.WriteAllText(Path.Combine(folder, "settings.toml"), "AllowLocalEndpoints = true\n");
        return folder;
    }

    private static async Task<CmdResult> Dispatch(PluginTestHost host, string command)
    {
        CmdResult result = await host.Client.Commands.DispatchAsync(command);
        Assert.True(result.Status == CmdStatus.Done, $"Command '{command}' failed: {result.Message}");
        return result;
    }

    private static string StatusText(PluginTestHost host)
    {
        try
        {
            CmdResult result = host.Client.Commands.DispatchAsync("alerts smart status")
                .GetAwaiter().GetResult();
            return result.Message ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                && File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root above " + AppContext.BaseDirectory);
    }

    /// <summary>Just enough OpenAI wire format: models plus verdict-bearing responses.</summary>
    private sealed class StubLlm : IDisposable
    {
        private readonly HttpListener _listener = new();
        private int _requests;

        public StubLlm()
        {
            int port = FreePort();
            Url = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        public string Url { get; }

        public int RequestCount => _requests;

        public void Dispose()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception)
            {
            }
        }

        private static int FreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        private async Task AcceptLoop()
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => Answer(context));
            }
        }

        private void Answer(HttpListenerContext context)
        {
            try
            {
                string body;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    body = reader.ReadToEnd();

                Interlocked.Increment(ref _requests);
                string payload = context.Request is { HttpMethod: "GET", Url: { AbsolutePath: "/v1/models" } }
                    ? """{"object":"list","data":[{"id":"stub-model"}]}"""
                    : VerdictPayload(body);
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception)
            {
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch (Exception)
                {
                }
            }
        }

        private static string VerdictPayload(string body)
        {
            if (body.Contains("verdicts", StringComparison.Ordinal))
            {
                var verdicts = new List<string>();
                foreach (string line in BatchLines(body))
                    verdicts.Add(VerdictFor(line));

                if (verdicts.Count == 0)
                    verdicts.Add(VerdictFor(body));

                return MessagePayload("{\"verdicts\":[" + string.Join(",", verdicts) + "]}");
            }

            return MessagePayload(VerdictFor(body));
        }

        /// <summary>Numbered batch lines ("0: ...", "1: ...") pulled out of the request JSON.</summary>
        private static List<string> BatchLines(string body)
        {
            var lines = new List<string>();
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                CollectLines(document.RootElement, lines);
            }
            catch (JsonException)
            {
            }

            return lines.Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d+: ")).ToList();
        }

        private static void CollectLines(JsonElement element, List<string> lines)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    lines.AddRange((element.GetString() ?? string.Empty)
                        .Split('\n').Select(l => l.Trim()));
                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray())
                        CollectLines(item, lines);

                    break;
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                        CollectLines(property.Value, lines);

                    break;
            }
        }

        private static string VerdictFor(string text)
        {
            bool yes = text.Contains("ALERT-YES", StringComparison.Ordinal);
            string routes = text.Contains("ONLY-DESKTOP", StringComparison.Ordinal)
                ? """["desktop"]"""
                : """["desktop","file"]""";
            return yes
                ? $"{{\"alert\":true,\"confidence\":0.95,\"reason\":\"test marker\",\"routes\":{routes}}}"
                : """{"alert":false,"confidence":0.9,"reason":"nothing here","routes":[]}""";
        }

        private static string MessagePayload(string text) =>
            "{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"model\":\"stub-model\",\"status\":\"completed\","
            + "\"output\":[{\"type\":\"message\",\"id\":\"m_1\",\"status\":\"completed\",\"role\":\"assistant\","
            + "\"content\":[{\"type\":\"output_text\",\"text\":" + JsonText(text) + ",\"annotations\":[]}]}],"
            + "\"usage\":{\"input_tokens\":5,\"output_tokens\":3,\"total_tokens\":8}}";

        private static string JsonText(string text)
            => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
