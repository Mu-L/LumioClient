using Lumio.Client.Replica;
using Lumio.Client.Replica.Tests.Support;
using Lumio.GameRuntime.Ecs;

namespace Lumio.Client.Replica.Tests.Unit;

public sealed class ReplicaFullSnapshotRebuildTests
{
    [Fact]
    public void FullSnapshotAppliesRuntimeCreateRecords()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        NetEntityId self = new(1UL, 101UL);
        Assert.True(consumer.Replica.TryObserveWelcome(WireCodec.EncodePack(new WelcomeMessage(1UL, self, 1UL))));
        Assert.True(CommitWorldChange(
            consumer.Replica,
            1UL,
            new WorldChangeMessage(
                1UL,
                0UL,
                new[]
                {
                    new CreateRecord("WorldEntity", new NetEntityId(1UL, 1UL), Array.Empty<FieldValue>()),
                    new CreateRecord("PlayerEntity", self, Array.Empty<FieldValue>()),
                    new CreateRecord("BotEntity", new NetEntityId(1UL, 102UL), Array.Empty<FieldValue>()),
                },
                Array.Empty<FieldChange>(),
                Array.Empty<DestroyRecord>(),
                Array.Empty<ClientRpcRecord>())));

        IReadOnlyList<ReplicaIdentityRecord> census = consumer.World.CopyIdentityRecords();
        Assert.Equal(2, census.Count);
        Assert.Equal(new[] { GameplayWireFixtures.RuntimeId(101), GameplayWireFixtures.RuntimeId(102) }, census.Select(r => r.NetEntityId).ToArray());
        Assert.Equal(new[] { "player", "bot" }, census.Select(r => r.EntityType).ToArray());
        Assert.Equal(new[] { string.Empty, string.Empty }, census.Select(r => r.UnmappedMark).ToArray());
        Assert.Equal(2, consumer.World.VisibleEntityCount);
        Assert.Empty(consumer.ChatWindow);
        Assert.True(consumer.World.InputEnabled);
        Assert.Equal(
            string.Empty,
            consumer.World.QueryAttribute(
                new ReplicaAttributeQuery("client-replica", "room-01", GameplayWireFixtures.RuntimeId(101), "IdentityComponent.name")).Value);
        Assert.Equal(
            ReplicaQueryStatus.RequestError,
            consumer.World.QueryAttribute(
                new ReplicaAttributeQuery("client-replica", "room-01", GameplayWireFixtures.RuntimeId(101), "EntityIdentity.unmappedMark")).Status);
        Assert.Equal(
            ReplicaQueryStatus.RequestError,
            consumer.World.QueryAttribute(
                new ReplicaAttributeQuery("client-replica", "room-01", GameplayWireFixtures.RuntimeId(102), "EntityIdentity.entityType")).Status);
    }

    [Fact]
    public void FullSnapshotOnNewGenerationDropsOldGenerationEntities()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Bot);
        NetEntityId firstSelf = new(1UL, 2UL);
        Assert.True(consumer.Replica.TryObserveWelcome(WireCodec.EncodePack(new WelcomeMessage(1UL, firstSelf, 1UL))));
        Assert.True(CommitWorldChange(consumer.Replica, 1UL, InitialChange(firstSelf, new NetEntityId(1UL, 3UL))));
        Assert.Equal(2, consumer.World.CopyIdentityRecords().Count);

        Assert.True(consumer.Replica.ResetForNewSession(new ReplicaResetRequest(2)).Succeeded);
        Assert.Equal(0, consumer.World.VisibleEntityCount);
        Assert.False(consumer.World.SelfLookup().Found);
        Assert.False(consumer.World.InputEnabled);

        NetEntityId secondSelf = new(1UL, 101UL);
        Assert.True(consumer.Replica.TryObserveWelcome(WireCodec.EncodePack(new WelcomeMessage(1UL, secondSelf, 2UL))));
        Assert.True(CommitWorldChange(consumer.Replica, 2UL, InitialChange(secondSelf, new NetEntityId(1UL, 102UL))));

        Assert.Equal(2, consumer.World.CopyIdentityRecords().Count);
        Assert.Equal(
            ReplicaQueryStatus.StaleGeneration,
            consumer.World.QueryAttribute(
                new ReplicaAttributeQuery(
                    "client-replica",
                    "room-01",
                    GameplayWireFixtures.RuntimeId(101),
                    "IdentityComponent.name",
                    1,
                    true,
                    string.Empty,
                    false)).Status);
        Assert.Equal(
            ReplicaQueryStatus.Ok,
            consumer.World.QueryAttribute(
                new ReplicaAttributeQuery("client-replica", "room-01", GameplayWireFixtures.RuntimeId(101), "IdentityComponent.name")).Status);
        Assert.Empty(consumer.ChatWindow);
        Assert.True(consumer.World.InputEnabled);
    }

    [Fact]
    public void UnsortedIdentityRecordsAreRejected()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        Assert.True(GameplayWireFixtures.AdmitRoom(consumer.Replica));
        ReplicaStageStatus staged = GameplayWireFixtures.StageJson(
            consumer.Replica,
            ReplicaUpdateKind.FullSnapshot,
            "{\"messageType\":\"FullSnapshot\",\"tickId\":7,\"revision\":1,\"stateBlocks\":[]}",
            1,
            10,
            0,
            0,
            out _);
        Assert.Equal(ReplicaStageStatus.Rejected, staged);
        Assert.Equal("bad_envelope", consumer.World.LastRejectCode);
    }

    [Fact]
    public void IllegalIdentityEntityTypeIsRejected()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Bot);
        Assert.True(GameplayWireFixtures.AdmitRoom(consumer.Replica));
        ReplicaStageStatus staged = GameplayWireFixtures.StageJson(
            consumer.Replica,
            ReplicaUpdateKind.FullSnapshot,
            "{\"messageType\":\"FullSnapshot\",\"tickId\":7,\"revision\":1,\"stateBlocks\":[]}",
            1,
            10,
            0,
            0,
            out _);
        Assert.Equal(ReplicaStageStatus.Rejected, staged);
        Assert.Equal("bad_envelope", consumer.World.LastRejectCode);
    }

    [Fact]
    public void EmptyStateBlocksAreZeroLiveCensusNotAPlaceholder()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        NetEntityId self = new(1UL, 2UL);
        Assert.True(consumer.Replica.TryObserveWelcome(WireCodec.EncodePack(new WelcomeMessage(1UL, self, 1UL))));
        Assert.True(CommitWorldChange(
            consumer.Replica,
            1UL,
            new WorldChangeMessage(
                1UL,
                0UL,
                new[] { new CreateRecord("WorldEntity", new NetEntityId(1UL, 1UL), Array.Empty<FieldValue>()) },
                Array.Empty<FieldChange>(),
                Array.Empty<DestroyRecord>(),
                Array.Empty<ClientRpcRecord>())));
        Assert.Empty(consumer.World.CopyIdentityRecords());
        Assert.Equal(0, consumer.World.VisibleEntityCount);
        Assert.True(consumer.World.SelfLookup().Found);
        Assert.True(consumer.World.InputEnabled);
    }

    private static WorldChangeMessage InitialChange(NetEntityId self, NetEntityId other)
        => new(
            1UL,
            0UL,
            new[]
            {
                new CreateRecord("WorldEntity", new NetEntityId(self.InstanceId, 1UL), Array.Empty<FieldValue>()),
                new CreateRecord("PlayerEntity", self, Array.Empty<FieldValue>()),
                new CreateRecord("BotEntity", other, Array.Empty<FieldValue>()),
            },
            Array.Empty<FieldChange>(),
            Array.Empty<DestroyRecord>(),
            Array.Empty<ClientRpcRecord>());

    private static bool CommitWorldChange(IClientReplica replica, ulong generation, WorldChangeMessage change)
    {
        var request = new ReplicaStageRequest(
            generation,
            ReplicaUpdateKind.FullSnapshot,
            0UL,
            0UL,
            1UL,
            1UL,
            WireCodec.EncodePack(change),
            Array.Empty<ulong>(),
            Array.Empty<ulong>());
        if (replica.StageAuthority(in request, out ReplicaStageHandle handle, out _).Status != ReplicaStageStatus.Staged)
        {
            return false;
        }

        return replica.ObserveRuntimeOutcome(
            handle,
            ReplicaRuntimeOutcome.CommittedOutcome(),
            out _) == ReplicaOutcomeStatus.Observed;
    }
}
