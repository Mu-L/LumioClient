using Lumio.Client.Replica;
using Lumio.Client.Replica.Tests.Support;
using Lumio.GameRuntime.Ecs;

namespace Lumio.Client.Replica.Tests.Fault;

public sealed class ReplicaMalformedChatTests
{
    [Fact]
    public void MalformedOrUnauthorizedEventsDoNotMutateReplicaWorldOrChatWindow()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        ReplicaVisibleEntity bot = GameplayWireFixtures.Entity("101", "bot", "room-01", 1, 1, 0);
        Assert.True(GameplayWireFixtures.AdmitRoom(consumer.Replica, extras: new[] { bot }));
        Assert.True(GameplayWireFixtures.CommitEmptySnapshot(consumer.Replica));
        ReplicaBindingLookup beforeBinding = consumer.World.SelfLookup();
        int beforeEntities = consumer.World.VisibleEntityCount;
        ReplicaCommittedMetadata beforeMeta = consumer.Replica.GetSnapshot().Committed;

        AssertRejected(consumer, ReplicaUpdateKind.FullSnapshot, GameplayWireFixtures.SnapshotWithChatEvent(), 2, 10, 0, 1);
        AssertRejected(consumer, ReplicaUpdateKind.Delta, GameplayWireFixtures.DeltaWithComponent(), 2, 10, 0, 1);
        AssertRejected(consumer, ReplicaUpdateKind.Delta, GameplayWireFixtures.BadHashDelta(), 2, 10, 0, 1);
        AssertRejected(consumer, ReplicaUpdateKind.Delta, GameplayWireFixtures.InputCommand(), 2, 10, 0, 1);
        AssertRejected(consumer, ReplicaUpdateKind.Delta, "{\"messageType\":\"Nope\"}", 2, 10, 0, 1);

        ReplicaBindingLookup afterBinding = consumer.World.SelfLookup();
        Assert.Equal(beforeBinding.Binding.NetEntityId, afterBinding.Binding.NetEntityId);
        Assert.Equal(beforeEntities, consumer.World.VisibleEntityCount);
        Assert.Empty(consumer.ChatWindow);
        ReplicaCommittedMetadata afterMeta = consumer.Replica.GetSnapshot().Committed;
        Assert.Equal(beforeMeta.Revision, afterMeta.Revision);
        Assert.Equal(beforeMeta.Sequence, afterMeta.Sequence);
        Assert.Equal(beforeMeta.HasBaseline, afterMeta.HasBaseline);
    }

    [Fact]
    public void RoomSequenceRegressionDoesNotAppend()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Bot);
        Assert.True(GameplayWireFixtures.AdmitRoom(
            consumer.Replica,
            extras: new[] { GameplayWireFixtures.Entity("101", "bot", "room-01", 1, 1, 0) }));
        Assert.True(GameplayWireFixtures.CommitEmptySnapshot(consumer.Replica));
        Assert.True(GameplayWireFixtures.CommitJson(
            consumer.Replica,
            ReplicaUpdateKind.Delta,
            GameplayWireFixtures.ContractChatDelta(),
            2,
            10,
            0,
            1));
        Assert.Single(consumer.ChatWindow);

        (string payload, string sha) = GameplayWireFixtures.EncodeChatEvent(1, 1, 101, "dup", 8);
        ReplicaStageStatus staged = GameplayWireFixtures.StageJson(
            consumer.Replica,
            ReplicaUpdateKind.Delta,
            GameplayWireFixtures.ChatDelta(payload, sha, 8, 2),
            3,
            10,
            1,
            2,
            out _);
        Assert.Equal(ReplicaStageStatus.Rejected, staged);
        Assert.Single(consumer.ChatWindow);
        Assert.Equal("gg", consumer.ChatWindow[0].Text);
        Assert.Equal("bad_envelope", consumer.World.LastRejectCode);
    }

    [Fact]
    public void OrderedChatRpcsInOneDeltaAdvanceTheLocalCursorPerRpc()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        Assert.True(GameplayWireFixtures.AdmitRoom(
            consumer.Replica,
            extras: new[] { GameplayWireFixtures.Entity("101", "bot", "room-01", 1, 1, 0) }));
        Assert.True(GameplayWireFixtures.CommitEmptySnapshot(consumer.Replica));
        Assert.True(GameplayWireFixtures.CommitJson(
            consumer.Replica,
            ReplicaUpdateKind.Delta,
            GameplayWireFixtures.ContractChatDelta(),
            2,
            10,
            0,
            1));

        byte[] frame = WireCodec.EncodePack(new WorldChangeMessage(
            8,
            0,
            Array.Empty<CreateRecord>(),
            Array.Empty<FieldChange>(),
            Array.Empty<DestroyRecord>(),
            new[]
            {
                new ClientRpcRecord(
                    new NetEntityId(1, 101),
                    "ChatComponent",
                    "OnChatMessage",
                    new object?[] { "second" },
                    2,
                    2,
                    new NetEntityId(1, 101),
                    8),
                new ClientRpcRecord(
                    new NetEntityId(1, 101),
                    "ChatComponent",
                    "OnChatMessage",
                    new object?[] { "third" },
                    3,
                    3,
                    new NetEntityId(1, 101),
                    8)
            }));

        ReplicaStageStatus staged = GameplayWireFixtures.StageJson(
            consumer.Replica,
            ReplicaUpdateKind.Delta,
            System.Text.Encoding.UTF8.GetString(frame),
            3,
            10,
            1,
            2,
            out ReplicaStageHandle handle);

        Assert.Equal(ReplicaStageStatus.Staged, staged);
        Assert.Equal(
            ReplicaOutcomeStatus.Observed,
            consumer.Replica.ObserveRuntimeOutcome(handle, ReplicaRuntimeOutcome.CommittedOutcome(), out _));
        Assert.Equal(new[] { "gg", "second", "third" }, consumer.ChatWindow.Select(line => line.Text).ToArray());
        Assert.Equal(3UL, consumer.ChatWindow[^1].MessageId);
        Assert.Equal(3UL, consumer.ChatWindow[^1].RoomSequence);
    }

    [Fact]
    public void UnknownCreateTypeFailsClosedWithoutMetadataOrWorldMutation()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        Assert.True(GameplayWireFixtures.AdmitRoom(consumer.Replica));
        Assert.True(GameplayWireFixtures.CommitEmptySnapshot(consumer.Replica));
        ReplicaCommittedMetadata before = consumer.Replica.GetSnapshot().Committed;
        int beforeEntities = consumer.World.VisibleEntityCount;
        byte[] frame = WireCodec.EncodePack(new WorldChangeMessage(
            9,
            0,
            new[] { new CreateRecord("unregistered.create.type", new NetEntityId(1, 999), Array.Empty<FieldValue>()) },
            Array.Empty<FieldChange>(),
            Array.Empty<DestroyRecord>(),
            Array.Empty<ClientRpcRecord>()));
        ReplicaStageRequest request = new(
            1,
            ReplicaUpdateKind.Delta,
            10,
            0,
            1,
            2,
            frame,
            Array.Empty<ulong>(),
            Array.Empty<ulong>());

        ReplicaStageStatus staged = consumer.Replica.StageAuthority(in request, out ReplicaStageHandle handle, out _).Status;
        Assert.Equal(ReplicaStageStatus.Staged, staged);
        ReplicaOutcomeStatus outcome = ReplicaOutcomeStatus.Observed;
        Exception? error = Record.Exception(() =>
        {
            outcome = consumer.Replica.ObserveRuntimeOutcome(handle, ReplicaRuntimeOutcome.CommittedOutcome(), out _);
        });

        Assert.Null(error);
        Assert.Equal(ReplicaOutcomeStatus.Rejected, outcome);
        Assert.Equal("bad_envelope", consumer.World.LastRejectCode);
        Assert.Equal(beforeEntities, consumer.World.VisibleEntityCount);
        ReplicaCommittedMetadata after = consumer.Replica.GetSnapshot().Committed;
        Assert.Equal(before.Baseline, after.Baseline);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Sequence, after.Sequence);
        Assert.Equal(before.HasBaseline, after.HasBaseline);
    }

    [Fact]
    public void TombstonedSenderRoomEventStillAppends()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        Assert.True(GameplayWireFixtures.AdmitRoom(
            consumer.Replica,
            extras: new[] { GameplayWireFixtures.Entity("101", "bot", "room-01", 1, 1, 0, tombstoned: true) }));
        Assert.True(GameplayWireFixtures.CommitJson(
            consumer.Replica,
            ReplicaUpdateKind.FullSnapshot,
            GameplayWireFixtures.IdentityCensus((1, "player", ""), (101, "bot", "")),
            1,
            10,
            0,
            0,
            new ulong[] { 101UL }));
        Assert.True(GameplayWireFixtures.CommitJson(
            consumer.Replica,
            ReplicaUpdateKind.Delta,
            GameplayWireFixtures.ContractChatDelta(),
            2,
            10,
            0,
            1));
        Assert.Single(consumer.ChatWindow);
        Assert.Equal(1UL, consumer.ChatWindow[0].MessageId);
        Assert.Equal(1UL, consumer.ChatWindow[0].RoomSequence);
        Assert.Equal(GameplayWireFixtures.RuntimeId(101), consumer.ChatWindow[0].SenderNetEntityId);
        Assert.Equal(
            ReplicaQueryStatus.Tombstoned,
            consumer.World.QueryAttribute(new ReplicaAttributeQuery("client-replica", "room-01", GameplayWireFixtures.RuntimeId(101), "IdentityComponent.name")).Status);
    }

    [Fact]
    public void InvisibleSenderRoomEventStillAppends()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        Assert.True(GameplayWireFixtures.AdmitRoom(
            consumer.Replica,
            extras: new[] { GameplayWireFixtures.Entity("101", "bot", "room-01", 1, 1, 0, inAoi: false) }));
        Assert.True(GameplayWireFixtures.CommitCensus(consumer.Replica, (1, "player", "")));
        Assert.True(GameplayWireFixtures.CommitJson(
            consumer.Replica,
            ReplicaUpdateKind.Delta,
            GameplayWireFixtures.ContractChatDelta(),
            2,
            10,
            0,
            1));
        Assert.Single(consumer.ChatWindow);
        Assert.Equal(1UL, consumer.ChatWindow[0].MessageId);
        Assert.Equal(1UL, consumer.ChatWindow[0].RoomSequence);
        Assert.Equal(GameplayWireFixtures.RuntimeId(101), consumer.ChatWindow[0].SenderNetEntityId);
        Assert.Equal(
            ReplicaQueryStatus.NonExistent,
            consumer.World.QueryAttribute(new ReplicaAttributeQuery("client-replica", "room-01", GameplayWireFixtures.RuntimeId(101), "IdentityComponent.name")).Status);
    }

    private static void AssertRejected(
        ReplicaChatConsumer consumer,
        ReplicaUpdateKind kind,
        string json,
        ulong sequence,
        ulong baseline,
        ulong fromRevision,
        ulong toRevision)
    {
        ReplicaStageStatus status = GameplayWireFixtures.StageJson(
            consumer.Replica,
            kind,
            json,
            sequence,
            baseline,
            fromRevision,
            toRevision,
            out ReplicaStageHandle handle);
        Assert.Equal(ReplicaStageStatus.Rejected, status);
        Assert.True(handle.IsEmpty);
        Assert.Empty(consumer.ChatWindow);
    }
}
