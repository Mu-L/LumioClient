using System;
using System.Text;
using Lumio.Client.Replica;
using Lumio.Client.Replica.Tests.Support;
using Lumio.GameRuntime.Ecs;

namespace Lumio.Client.Replica.Tests.Fault;

public sealed class ReplicaRuntimeRpcContractTests
{
    [Fact]
    public void RoomRpcWithWrongTargetIsRejectedBeforePresentation()
    {
        ReplicaChatConsumer consumer = CreateReadyConsumer();
        string frame = RuntimeChatDelta(new NetEntityId(1, 102), new NetEntityId(1, 101), Scope.Room);

        ReplicaStageStatus status = StageDelta(consumer, frame, out ReplicaStageHandle handle);

        Assert.Equal(ReplicaStageStatus.Rejected, status);
        Assert.True(handle.IsEmpty);
        Assert.Empty(consumer.ChatWindow);
    }

    [Fact]
    public void RoomRpcWithDefaultTargetIsRejectedBeforePresentation()
    {
        ReplicaChatConsumer consumer = CreateReadyConsumer();
        string frame = RuntimeChatDelta(default, new NetEntityId(1, 101), Scope.Room);

        ReplicaStageStatus status = StageDelta(consumer, frame, out ReplicaStageHandle handle);

        Assert.Equal(ReplicaStageStatus.Rejected, status);
        Assert.True(handle.IsEmpty);
        Assert.Empty(consumer.ChatWindow);
    }

    [Fact]
    public void NonRoomRpcScopeIsRejectedBeforePresentation()
    {
        ReplicaChatConsumer consumer = CreateReadyConsumer();
        NetEntityId sender = new(1, 101);
        string frame = RuntimeChatDelta(sender, sender, Scope.Owner);

        ReplicaStageStatus status = StageDelta(consumer, frame, out ReplicaStageHandle handle);

        Assert.Equal(ReplicaStageStatus.Rejected, status);
        Assert.True(handle.IsEmpty);
        Assert.Empty(consumer.ChatWindow);
    }

    [Fact]
    public void CanonicalRuntimeRoomRpcIsPresented()
    {
        ReplicaChatConsumer consumer = CreateReadyConsumer();
        NetEntityId sender = new(1, 101);
        string frame = RuntimeChatDelta(sender, sender, Scope.Room);

        ReplicaStageStatus status = StageDelta(consumer, frame, out ReplicaStageHandle handle);

        Assert.Equal(ReplicaStageStatus.Staged, status);
        Assert.Equal(
            ReplicaOutcomeStatus.Observed,
            consumer.Replica.ObserveRuntimeOutcome(handle, ReplicaRuntimeOutcome.CommittedOutcome(), out _));
        Assert.Single(consumer.ChatWindow);
        Assert.Equal("hello", consumer.ChatWindow[0].Text);
    }

    private static ReplicaChatConsumer CreateReadyConsumer()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        Assert.True(GameplayWireFixtures.AdmitRoom(
            consumer.Replica,
            extras: new[] { GameplayWireFixtures.Entity("101", "bot", "room-01", 1, 1, 0) }));
        Assert.True(GameplayWireFixtures.CommitEmptySnapshot(consumer.Replica));
        return consumer;
    }

    private static ReplicaStageStatus StageDelta(
        ReplicaChatConsumer consumer,
        string frame,
        out ReplicaStageHandle handle)
    {
        return GameplayWireFixtures.StageJson(
            consumer.Replica,
            ReplicaUpdateKind.Delta,
            frame,
            2,
            10,
            0,
            1,
            out handle);
    }

    private static string RuntimeChatDelta(NetEntityId target, NetEntityId sender, Scope scope)
    {
        byte[] frame = WireCodec.EncodePack(new WorldChangeMessage(
            8,
            0,
            Array.Empty<CreateRecord>(),
            Array.Empty<FieldChange>(),
            Array.Empty<DestroyRecord>(),
            new[]
            {
                new ClientRpcRecord(
                    target,
                    "ChatComponent",
                    "OnChatMessage",
                    new object?[] { "hello" },
                    1,
                    1,
                    sender,
                    8,
                    scope)
            }));
        return Encoding.UTF8.GetString(frame);
    }
}
