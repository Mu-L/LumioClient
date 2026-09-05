using System.Text;
using Lumio.Client.Replica;
using Lumio.Client.Replica.Tests.Support;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Replication.Binding;
using Lumio.GameRuntime.Samples.Username.Components.Identity;
using Lumio.GameRuntime.Samples.Username.EntityTypes;

namespace Lumio.Client.Replica.Tests.Unit;

public sealed class ReplicaWorldRuntimeTests
{
    [Fact]
    public void ReplicaNetIdsAcceptOnlyRuntimeIssued128BitHex()
    {
        Assert.False(ReplicaNetIds.TryParse("101", out _));
        Assert.False(ReplicaNetIds.TryParse(new NetEntityId(0UL, 0UL).ToHex(), out _));
        Assert.Equal(
            "00000000000000010000000000000065",
            ReplicaNetIds.Format(new NetEntityId(1UL, 101UL)));
    }

    [Fact]
    public void RuntimeWelcomeRejectsHostShortIdentity()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        byte[] frame = Encoding.UTF8.GetBytes("{\"messageType\":\"Welcome\",\"instanceId\":1,\"selfNetEntityId\":\"1\",\"connectionGeneration\":1}");
        Assert.False(consumer.Replica.TryObserveWelcome(frame));
    }

    [Fact]
    public void RuntimeWelcomePreservesRuntimeIssuedInstanceId()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        string self = new NetEntityId(7UL, 1UL).ToHex();
        string bot = new NetEntityId(7UL, 101UL).ToHex();
        Assert.True(GameplayWireFixtures.AdmitRoom(consumer.Replica, selfId: self, extras: new[] { GameplayWireFixtures.Entity(bot, "bot", "room-01", 1, 1, 0) }));
        Assert.Equal(7UL, consumer.World.Manager.World.InstanceId);
        Assert.Equal(
            ReplicaQueryStatus.Ok,
            consumer.World.QueryAttribute(
                new ReplicaAttributeQuery("client-replica", "room-01", bot, "IdentityComponent.name")).Status);
    }

    [Fact]
    public void RuntimeWorldChangePreservesNonzeroHigh64Ids()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        string self = new NetEntityId(0x1122334455667788UL, 1UL).ToHex();
        string bot = new NetEntityId(0x1122334455667788UL, 101UL).ToHex();
        Assert.True(GameplayWireFixtures.AdmitRoom(consumer.Replica, selfId: self));

        byte[] frame = WireCodec.EncodePack(new WorldChangeMessage(
            7UL,
            0UL,
            new[]
            {
                new CreateRecord("player", NetEntityId.Parse(self), Array.Empty<FieldValue>()),
                new CreateRecord("bot", NetEntityId.Parse(bot), Array.Empty<FieldValue>())
            },
            Array.Empty<FieldChange>(),
            Array.Empty<DestroyRecord>(),
            Array.Empty<ClientRpcRecord>()));
        ReplicaStageStatus staged = consumer.Replica.StageAuthority(
            new ReplicaStageRequest(1UL, ReplicaUpdateKind.FullSnapshot, 10UL, 0UL, 1UL, 1UL, frame, Array.Empty<ulong>(), Array.Empty<ulong>()),
            out ReplicaStageHandle handle,
            out _).Status;

        Assert.Equal(ReplicaStageStatus.Staged, staged);
        Assert.Equal(
            ReplicaOutcomeStatus.Observed,
            consumer.Replica.ObserveRuntimeOutcome(handle, ReplicaRuntimeOutcome.CommittedOutcome(), out _));
        Assert.Contains(consumer.World.CopyIdentityRecords(), record => record.NetEntityId == bot);
    }

    [Fact]
    public void RuntimeWorldChangeRejectsDecimalIdentity()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        byte[] frame = Encoding.UTF8.GetBytes(
            "{\"creates\":[{\"entityType\":\"bot\",\"fields\":[],\"netEntityId\":101}],\"destroys\":[],\"fields\":[],\"messageType\":\"WorldChange\",\"rpcs\":[],\"tick\":1}");

        ReplicaStageStatus staged = consumer.Replica.StageAuthority(
            new ReplicaStageRequest(1UL, ReplicaUpdateKind.FullSnapshot, 10UL, 0UL, 1UL, 1UL, frame, Array.Empty<ulong>(), Array.Empty<ulong>()),
            out _,
            out _).Status;

        Assert.Equal(ReplicaStageStatus.Rejected, staged);
        Assert.False(consumer.World.InputEnabled);
    }

    [Fact]
    public void ReplicaQueryRejectsMalformedIdentityWithoutLocalFallback()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        Assert.True(consumer.Replica.TryObserveWelcome(WireCodec.EncodePack(new WelcomeMessage(1, new NetEntityId(1, 1), 1))));

        ReplicaAttributeQueryResult result = consumer.World.QueryAttribute(
            new ReplicaAttributeQuery("client-replica", "room-01", "1", "IdentityComponent.name"));

        Assert.Equal(ReplicaQueryStatus.RequestError, result.Status);
        Assert.Equal("invalid_binding_shape", result.Code);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void RuntimeEncodedConnectionSupersededFrameIsDecodedByRuntimeCodec()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        byte[] frame = WireCodec.EncodePack(new ConnectionSupersededMessage(new NetEntityId(0x1122334455667788UL, 1UL), 2UL));

        Assert.True(consumer.Replica.TryObserveConnectionSuperseded(frame, out ReplicaConnectionSuperseded notice));
        Assert.True(notice.Received);
        Assert.Equal("11223344556677880000000000000001", notice.NetEntityId);
        Assert.Equal(2UL, notice.NewConnectionGeneration);
        Assert.False(consumer.World.InputEnabled);
    }

    [Fact]
    public void ClientReplicaAttributeReadsUseRuntimeQueryResultsAndPreserveC1OutboundFrames()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        Assert.True(GameplayWireFixtures.AdmitRoom(
            consumer.Replica,
            extras: new[] { GameplayWireFixtures.Entity("101", "bot", "room-01", 1, 1, 0) }));
        NetEntityId id = NetEntityId.Parse("00000000000000010000000000000065");
        consumer.World.Manager.World.Get<IdentityComponent>(id).Name.Value = "bot-name";

        ReplicaAttributeQueryResult result = consumer.World.QueryAttribute(
            new ReplicaAttributeQuery("client-replica", "room-01", GameplayWireFixtures.RuntimeId(101), "IdentityComponent.name"));

        Assert.Equal(ReplicaQueryStatus.Ok, result.Status);
        Assert.Equal("bot-name", result.Value);
        WorldDrainResponse drained = consumer.World.Drain();
        InputCommandMessage outbound = Assert.IsType<InputCommandMessage>(Assert.Single(drained.Frames));
        Assert.Equal("field.write", outbound.MappingId);
        Assert.Equal(outbound.MappingId, WireCodec.DecodeInput(WireCodec.EncodeInput(outbound), outbound.Sender).MappingId);
        Assert.Empty(drained.Queries);
    }

    [Fact]
    public void ReplicaWorldDrainKeepsRuntimeFramesAndInternalQueriesSeparate()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        Assert.True(GameplayWireFixtures.AdmitRoom(consumer.Replica));
        NetEntityId self = NetEntityId.Parse("00000000000000010000000000000001");
        consumer.World.Manager.Enqueue(new AttributeQueryMessage(
            "query-1",
            "client-replica",
            "room-01",
            self,
            "IdentityComponent.name"));
        consumer.World.Manager.Tick();

        WorldDrainResponse drained = consumer.World.Drain();

        Assert.Empty(drained.Frames);
        AttributeQueryResult result = Assert.IsType<AttributeQueryResult>(Assert.Single(drained.Queries));
        Assert.Equal("query-1", result.RequestId);
        Assert.Equal("ok", result.Outcome);
    }

    [Fact]
    public void CreateRecordRunsAwakePostAttributeStart()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        WorldManager manager = consumer.World.Manager;
        ulong instance = manager.World.InstanceId;
        var worldEntity = new NetEntityId(instance, 1);
        var player = new NetEntityId(instance, 2);
        manager.Enqueue(new WelcomeMessage(instance, player, "self"));
        manager.Enqueue(new WorldChangeMessage(
            1UL,
            0UL,
            new[]
            {
                new CreateRecord("WorldEntity", worldEntity, Array.Empty<FieldValue>()),
                new CreateRecord("PlayerEntity", player, Array.Empty<FieldValue>())
            },
            Array.Empty<FieldChange>(),
            Array.Empty<DestroyRecord>(),
            Array.Empty<ClientRpcRecord>()));
        manager.Tick();

        Assert.Equal(new[] { "Awake", "PostAttribute", "Start" }, manager.World.LifecycleOf(player).ToArray());
        Assert.True(manager.World.TypeOf(player).Is<PlayerEntity>());
        Assert.Equal(player, manager.World.Self.Id);
    }

    [Fact]
    public void NonC1FullSnapshotIsBadEnvelopeAndDoesNotEnableInput()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        Assert.True(consumer.Replica.TryObserveWelcome(WireCodec.EncodePack(new WelcomeMessage(1, new NetEntityId(1, 1), 1))));
        ReplicaStageStatus staged = GameplayWireFixtures.StageJson(
            consumer.Replica,
            ReplicaUpdateKind.FullSnapshot,
            "not-json",
            1,
            10,
            0,
            0,
            out _);
        Assert.Equal(ReplicaStageStatus.Rejected, staged);
        Assert.Equal("bad_envelope", consumer.World.LastRejectCode);
        Assert.False(consumer.World.InputEnabled);

        staged = consumer.Replica.StageAuthority(
            new ReplicaStageRequest(
                1,
                ReplicaUpdateKind.FullSnapshot,
                10,
                0,
                0,
                1,
                new byte[] { 0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE },
                Array.Empty<ulong>(),
                Array.Empty<ulong>()),
            out _,
            out _).Status;
        Assert.Equal(ReplicaStageStatus.Rejected, staged);
        Assert.Equal("bad_envelope", consumer.World.LastRejectCode);
        Assert.False(consumer.World.InputEnabled);
    }

    [Fact]
    public void MissingStateBlocksIsBadEnvelope()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        ReplicaStageStatus staged = GameplayWireFixtures.StageJson(
            consumer.Replica,
            ReplicaUpdateKind.FullSnapshot,
            "{\"messageType\":\"FullSnapshot\",\"tickId\":0,\"revision\":0}",
            1,
            10,
            0,
            0,
            out _);
        Assert.Equal(ReplicaStageStatus.Rejected, staged);
        Assert.Equal("bad_envelope", consumer.World.LastRejectCode);
    }

    [Fact]
    public void OwnerNameWriteProducesFieldWriteOutbound()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        Assert.True(GameplayWireFixtures.AdmitRoom(consumer.Replica));
        World world = consumer.World.Manager.World;
        world.Self.Get<IdentityComponent>().Name.Value = "ABCD";
        IReadOnlyList<WorldMessage> outbound = consumer.World.DrainOutbound();
        Assert.Contains(outbound, message => message is InputCommandMessage input
            && string.Equals(input.MappingId, "field.write", StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionSourcesHaveNoAttributeBag()
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
                if (text.Contains("Dictionary<string, string> Attributes", StringComparison.Ordinal)
                    || text.Contains("class AttributeDeclarationTable", StringComparison.Ordinal)
                    || text.Contains("RebuildFromIdentity", StringComparison.Ordinal))
                {
                    hits.Add(file);
                }
            }
        }

        Assert.Empty(hits);
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
}
