using Lumio.Client.Bot;
using Lumio.Client.Bot.Host;
using Lumio.Client.Bot.Tests.Support;
using Lumio.Client.Connection;
using Lumio.GameRuntime.Ecs;
using Lumio.Client.Handshake;
using Lumio.Client.Input;
using Lumio.Client.Observability;
using Lumio.Client.Persistence;
using Lumio.Client.Prediction;
using Lumio.Client.Replica;
using Lumio.Client.Session;
using Lumio.GameRuntime.Samples.Username.Components.Chat;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lumio.Client.Bot.Tests.Unit;

public sealed class BotCadenceTests
{
    [Fact]
    public void FoundationWorldChangeFixtureUsesC1AppliedInputSequenceAndDestroyRecords()
    {
        WorldChangeMessage change = Assert.IsType<WorldChangeMessage>(
            WireCodec.DecodePack(FoundationHostCommand.WorldChange));

        Assert.Equal(1UL, change.Tick);
        Assert.Equal(0UL, change.AppliedInputSequence);
        Assert.Empty(change.Fields);
        Assert.Empty(change.Destroys);
        Assert.Empty(change.Rpcs);
        Assert.Equal(2, change.Creates.Count);
    }

    [Fact]
    public void ClientTimerManagerFiresFiveTenFifteenOnTickFrameAdvance()
    {
        var abi = new C4TickFrameAbi();
        using var timer = new ClientTimerManager(abi);
        Assert.True(timer.ScheduleBotChatCadence());
        IReadOnlyList<ulong> dues = timer.Advance(15);
        Assert.Equal(new ulong[] { 5, 10, 15 }, dues.ToArray());
        Assert.Equal(new ulong[] { 5, 10, 15 }, timer.Trace.UtteranceTicks.ToArray());
    }

    [Fact]
    public async Task HeadlessBotHostSubmitsChatInputOnCadenceTicksOnly()
    {
        var abi = new C4TickFrameAbi();
        var timer = new ClientTimerManager(abi);
        var order = new List<string>();
        var host = new HeadlessBotHost(
            new RecordingSession(order),
            new RecordingDriver(order),
            new RecordingIngress(order),
            new NullHook(),
            timer);
        int code = await host.RunAsync(new BotRunRequest(15, 0), CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Equal(new ulong[] { 5, 10, 15 }, timer.Trace.UtteranceTicks.ToArray());
        Assert.Equal(3, order.Count(item => item == "fill"));
        Assert.Equal(host.SubmittedTicks.ToArray(), new ulong[] { 5, 10, 15 });
    }

    [Fact]
    public async Task ConnectionSupersededStopsChatInputWithoutReconnect()
    {
        var abi = new C4TickFrameAbi();
        var timer = new ClientTimerManager(abi);
        var host = new HeadlessBotHost(
            new RecordingSession(new List<string>()),
            new RecordingDriver(new List<string>()),
            new RecordingIngress(new List<string>()),
            new NullHook(),
            timer);
        host.StopInput("connection_superseded");
        int code = await host.RunAsync(new BotRunRequest(15, 0), CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Equal("connection_superseded", host.InputStopReason);
        Assert.Empty(host.SubmittedTicks);
        Assert.False(host.Reconnected);
    }

    [Fact]
    public async Task HeadlessBotHostSendsChatOnCadenceAfterCreateRecord()
    {
        IClientReplica replica = new ClientReplicaFactory().Create();
        replica.ResetForNewSession(new ReplicaResetRequest(1));
        IReplicaWorld world = replica.World;
        var expectedSelf = new NetEntityId(7UL, 2UL);
        Assert.True(CommitInitialWorld(replica, expectedSelf));
        NetEntityId self = world.Manager.World.Self.Id;
        Assert.True(world.InputEnabled);
        Assert.True(world.Manager.World.IsLive(self));

        var abi = new C4TickFrameAbi();
        var timer = new ClientTimerManager(abi);
        var host = new HeadlessBotHost(
            new RecordingSession(new List<string>()),
            new RecordingDriver(new List<string>()),
            new RecordingIngress(new List<string>()),
            new NullHook(),
            timer,
            world);
        int code = await host.RunAsync(new BotRunRequest(15, 0), CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Equal(new ulong[] { 5, 10, 15 }, host.SubmittedTicks.ToArray());
        IReadOnlyList<WorldMessage> outbound = world.DrainOutbound();
        Assert.Equal(3, outbound.Count(message => message is InputCommandMessage input
            && string.Equals(input.MappingId, "chat.input", StringComparison.Ordinal)));
    }

    [Fact]
    public void ProductionResidentLoopTicksAndLogsChatInputAfterAwait()
    {
        IClientReplica replica = new ClientReplicaFactory().Create();
        replica.ResetForNewSession(new ReplicaResetRequest(1));
        IReplicaWorld world = replica.World;
        var expectedSelf = new NetEntityId(7UL, 2UL);
        Assert.True(CommitInitialWorld(replica, expectedSelf));
        NetEntityId self = world.Manager.World.Self.Id;
        Assert.True(world.InputEnabled);

        string logDir = Path.Combine(Path.GetTempPath(), "lumio-bot-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDir);
        string logPath = Path.Combine(logDir, "bot-host.ndjson");
        string releaseFlag = Path.Combine(logDir, "release.flag");
        int owner = Environment.CurrentManagedThreadId;
        int delayCalls = 0;
        var threadIds = new List<int>();
        var abi = new C4TickFrameAbi();
        using var timer = new ClientTimerManager(abi);
        Assert.True(timer.ScheduleBotChatCadence());
        var evidence = Enumerable.Range(1, 6)
            .Select(index => new ProductionChatInputEvidence(
                logPath,
                "Bot" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
        var session = new WorldBackedSession(world, evidence);
        ResidentBot[] residents = Enumerable.Range(1, 6)
            .Select(index => new ResidentBot(
                "Bot" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
                session,
                evidence[index - 1]))
            .ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        int code = BotHostOwnerPump.Run(async () =>
        {
            await BotHostResidentLoop.RunAsync(
                residents,
                timer,
                logPath,
                releaseFlag,
                async cancellationToken =>
                {
                    threadIds.Add(Environment.CurrentManagedThreadId);
                    await Task.Delay(5, cancellationToken);
                    threadIds.Add(Environment.CurrentManagedThreadId);
                    delayCalls++;
                    if (delayCalls >= 30)
                    {
                        File.WriteAllText(releaseFlag, "1");
                    }
                },
                timeout.Token);
            return 0;
        });

        try
        {
            Assert.Equal(0, code);
            Assert.NotEmpty(threadIds);
            Assert.All(threadIds, id => Assert.Equal(owner, id));
            Assert.True(File.Exists(logPath));
            string log = File.ReadAllText(logPath);
            Assert.Contains("\"kind\":\"chat.input\"", log, StringComparison.Ordinal);
            Assert.Contains("\"tickSource\":\"native-kernel/tickFrame\"", log, StringComparison.Ordinal);
            Assert.Contains("\"accountId\":\"Bot01\"", log, StringComparison.Ordinal);
            Assert.Equal(6, File.ReadAllLines(logPath).Length);
            foreach (string line in File.ReadAllLines(logPath))
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement record = document.RootElement;
                Assert.Equal("InputCommand", record.GetProperty("messageType").GetString());
                Assert.Equal("chat.input", record.GetProperty("mappingId").GetString());
                Assert.Equal(64, record.GetProperty("payloadSha256").GetString()!.Length);
            }
            Assert.Equal(new ulong[] { 5, 10, 15 }, timer.Trace.UtteranceTicks.ToArray());
            Assert.Equal(6, session.OutboundInputCount);
        }
        finally
        {
            Directory.Delete(logDir, true);
        }
    }

    [Fact]
    public void ProductionBotUsesLongIdleWindowForRoomConnections()
    {
        string source = File.ReadAllText(
            Path.Combine(RepoRoot(), "modules", "bot", "host", "FoundationHostCommand.cs"));

        Assert.Contains("TimeSpan.FromMinutes(5)", source, StringComparison.Ordinal);
        Assert.Contains(
            "new WebSocketClientConnectionFactory(transportOptions)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionChatEvidenceHashesPayloadFromObservedWireBytes()
    {
        IClientReplica replica = new ClientReplicaFactory().Create();
        replica.ResetForNewSession(new ReplicaResetRequest(1));
        Assert.True(CommitInitialWorld(replica, new NetEntityId(7UL, 2UL)));
        IReplicaWorld world = replica.World;
        world.Manager.World.Self.Get<ChatComponent>().SendMessage("wire-evidence");
        world.Manager.Tick();
        InputCommandMessage message = Assert.IsType<InputCommandMessage>(Assert.Single(world.DrainOutbound()));
        byte[] encoded = WireCodec.EncodeInput(message);
        InputCommandMessage decoded = WireCodec.DecodeInput(encoded);

        string logPath = Path.Combine(Path.GetTempPath(), "lumio-bot-evidence-" + Guid.NewGuid().ToString("N") + ".ndjson");
        try
        {
            var evidence = new ProductionChatInputEvidence(logPath, "Bot01");
            evidence.ExpectChatInput(5UL);
            evidence.Observe(message, encoded);

            string log = File.ReadAllText(logPath);
            using JsonDocument document = JsonDocument.Parse(log);
            JsonElement record = document.RootElement;
            Assert.Equal("InputCommand", record.GetProperty("messageType").GetString());
            Assert.Equal(WireCodec.ChatInput, record.GetProperty("mappingId").GetString());
            string expectedHash = Convert.ToHexString(SHA256.HashData(decoded.Payload.Span)).ToLowerInvariant();
            Assert.Equal(expectedHash, record.GetProperty("payloadSha256").GetString());
            Assert.DoesNotContain("wire-evidence", log, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task MissingEngineNativeIsBlocked()
    {
        string logDir = Path.Combine(Path.GetTempPath(), "lumio-bot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDir);
        try
        {
            int code = await FoundationHostCommand.RunAsync(
                new[]
                {
                    "--server", "ws://127.0.0.1:1/session",
                    "--account-from", "Bot01",
                    "--account-to", "Bot01",
                    "--log-dir", logDir
                },
                CancellationToken.None);
            Assert.Equal(FoundationHostCommand.BlockedExitCode, code);
        }
        finally
        {
            Directory.Delete(logDir, true);
        }
    }

    [Fact]
    public void ProductionSourcesHaveNoSecondTimerOrBindingTable()
    {
        string repo = RepoRoot();
        string[] roots =
        {
            Path.Combine(repo, "modules", "replica", "src"),
            Path.Combine(repo, "modules", "bot", "src")
        };
        var hits = new List<string>();
        foreach (string root in roots)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                if (text.Contains("System.Timers.Timer", StringComparison.Ordinal)
                    || text.Contains("System.Threading.Timer", StringComparison.Ordinal)
                    || text.Contains("class BindingTable", StringComparison.Ordinal)
                    || text.Contains("new BindingTable", StringComparison.Ordinal))
                {
                    hits.Add(file);
                }
            }
        }

        Assert.Empty(hits);
    }

    private static bool CommitInitialWorld(IClientReplica replica, NetEntityId self)
    {
        if (!replica.TryObserveWelcome(WireCodec.EncodePack(
            new WelcomeMessage(self.InstanceId, self, 1UL))))
        {
            return false;
        }

        byte[] frame = WireCodec.EncodePack(new WorldChangeMessage(
            1UL,
            0UL,
            new[]
            {
                new CreateRecord("world", new NetEntityId(self.InstanceId, 1UL), Array.Empty<FieldValue>()),
                new CreateRecord("player", self, Array.Empty<FieldValue>()),
            },
            Array.Empty<FieldChange>(),
            Array.Empty<DestroyRecord>(),
            Array.Empty<ClientRpcRecord>()));
        var request = new ReplicaStageRequest(
            1,
            ReplicaUpdateKind.FullSnapshot,
            10,
            0,
            0,
            1,
            frame,
            Array.Empty<ulong>(),
            Array.Empty<ulong>());
        if (replica.StageAuthority(in request, out ReplicaStageHandle handle, out _).Status != ReplicaStageStatus.Staged)
        {
            return false;
        }

        return replica.ObserveRuntimeOutcome(handle, ReplicaRuntimeOutcome.CommittedOutcome(), out _) == ReplicaOutcomeStatus.Observed;
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LumioClient.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }

    private sealed class NullHook : IBotTickHook
    {
        public void BeforeTick(int tick)
        {
            _ = tick;
        }
    }

    private sealed class RecordingDriver : IBotScenarioDriver
    {
        private readonly List<string> _order;

        public RecordingDriver(List<string> order)
        {
            _order = order;
        }

        public int FillSamples(in BotDriverContext context, Span<RawInputSample> destination)
        {
            _ = context;
            _order.Add("fill");
            if (destination.Length == 0)
            {
                return 0;
            }

            destination[0] = new RawInputSample(1, 0, 0);
            return 1;
        }
    }

    private sealed class RecordingIngress : IInputSampleIngress
    {
        private readonly List<string> _order;

        public RecordingIngress(List<string> order)
        {
            _order = order;
        }

        public InputEnqueueReceipt TryEnqueue(in RawInputSample sample)
        {
            _ = sample;
            _order.Add("enqueue");
            return new InputEnqueueReceipt(true, default, default);
        }

        public SequencedInputSample[] DrainAccepted()
        {
            return Array.Empty<SequencedInputSample>();
        }
    }

    private sealed class WorldBackedSession : IClientSession
    {
        private readonly IReplicaWorld _world;
        private readonly ProductionChatInputEvidence[] _evidence;

        public int OutboundInputCount { get; private set; }

        public WorldBackedSession(IReplicaWorld world, ProductionChatInputEvidence[] evidence)
        {
            _world = world;
            _evidence = evidence;
        }

        public SessionCommandResult RequestConnect(in SessionConnectRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return new SessionCommandResult(true);
        }

        public SessionTickResult Tick(in ClientOwnerTick tick)
        {
            _ = tick;
            IReadOnlyList<WorldMessage> outbound = _world.DrainOutbound();
            int[] partition = { 0, 3, 1, 4, 2, 5 };
            int target = 0;
            for (int i = 0; i < outbound.Count; i++)
            {
                if (outbound[i] is not InputCommandMessage input)
                {
                    continue;
                }

                byte[] encoded = WireCodec.EncodeInput(input);
                _evidence[partition[target % partition.Length]].Observe(input, encoded);
                OutboundInputCount++;
                target++;
            }

            return new SessionTickResult(ClientSessionState.Active);
        }

        public SessionCommandResult RequestClose(in SessionCloseRequest request)
        {
            _ = request;
            return new SessionCommandResult(true);
        }

        public SessionCommandResult Login(in SessionConnectRequest request, CancellationToken cancellationToken)
        {
            return RequestConnect(in request, cancellationToken);
        }

        public bool TryDequeueSuperseded(out SessionSupersededNotice notice)
        {
            notice = default;
            return false;
        }

        public bool TryGetReplicaWorld(out IReplicaWorld world)
        {
            world = _world;
            return true;
        }

        public ClientSessionSnapshot GetSnapshot()
        {
            return new ClientSessionSnapshot(
                ClientSessionState.Active,
                1,
                true,
                0,
                true,
                true,
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                Array.Empty<string>());
        }
    }

    private sealed class RecordingSession : IClientSession
    {
        private readonly List<string> _order;
        private ClientSessionState _state = ClientSessionState.Disconnected;

        public RecordingSession(List<string> order)
        {
            _order = order;
        }

        public SessionCommandResult RequestConnect(in SessionConnectRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            _state = ClientSessionState.Negotiating;
            return new SessionCommandResult(true);
        }

        public SessionTickResult Tick(in ClientOwnerTick tick)
        {
            _ = tick;
            _order.Add("tick");
            return new SessionTickResult(_state);
        }

        public SessionCommandResult RequestClose(in SessionCloseRequest request)
        {
            _ = request;
            _state = ClientSessionState.Closed;
            return new SessionCommandResult(true);
        }

        public SessionCommandResult Login(in SessionConnectRequest request, CancellationToken cancellationToken)
        {
            return RequestConnect(in request, cancellationToken);
        }

        public bool TryDequeueSuperseded(out SessionSupersededNotice notice)
        {
            notice = default;
            return false;
        }

        public bool TryGetReplicaWorld(out IReplicaWorld world)
        {
            world = default!;
            return false;
        }

        public ClientSessionSnapshot GetSnapshot()
        {
            _order.Add("observe");
            return new ClientSessionSnapshot(
                _state,
                1,
                false,
                0,
                false,
                false,
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                Array.Empty<string>());
        }
    }
}
