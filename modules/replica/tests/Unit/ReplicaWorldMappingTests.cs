using System.Text;
using Lumio.Client.Replica;
using Lumio.Client.Replica.Tests.Support;

namespace Lumio.Client.Replica.Tests.Unit;

public sealed class ReplicaWorldMappingTests
{
    [Fact]
    public void EachClientOwnsAnIndependentReplicaWorld()
    {
        ReplicaChatConsumer browser = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        ReplicaChatConsumer bot = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Bot);
        Assert.NotSame(browser.Replica, bot.Replica);
        Assert.NotSame(browser.World, bot.World);
        Assert.True(GameplayWireFixtures.AdmitRoom(browser.Replica));
        Assert.True(GameplayWireFixtures.AdmitRoom(bot.Replica, "2", "bot"));
        Assert.Equal(GameplayWireFixtures.RuntimeId(1), browser.World.SelfLookup().Binding.NetEntityId);
        Assert.Equal(GameplayWireFixtures.RuntimeId(2), bot.World.SelfLookup().Binding.NetEntityId);
        Assert.Equal("bot", bot.World.SelfLookup().Binding.EntityType);
    }

    [Fact]
    public void ClientQueryReadsOnlyReplicatedVisibleAttributes()
    {
        ReplicaChatConsumer browser = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        Assert.True(GameplayWireFixtures.AdmitRoom(
            browser.Replica,
            extras: new[] { GameplayWireFixtures.Entity("101", "bot", "room-01", 1, 4, 7) }));

        ReplicaAttributeQueryResult type = browser.World.QueryAttribute(
            new ReplicaAttributeQuery("client-replica", "room-01", GameplayWireFixtures.RuntimeId(101), "IdentityComponent.name"));
        Assert.Equal(ReplicaQueryStatus.Ok, type.Status);
        Assert.Equal(string.Empty, type.Value);

        ReplicaAttributeQueryResult persistOnly = browser.World.QueryAttribute(
            new ReplicaAttributeQuery("client-replica", "room-01", GameplayWireFixtures.RuntimeId(101), "ChatComponent.lastMessagePersistOnly"));
        Assert.Equal(ReplicaQueryStatus.RequestError, persistOnly.Status);
        Assert.Equal("undeclared_attribute", persistOnly.Code);
    }

    [Fact]
    public void ForbiddenBindingShapeDoesNotMutateWorld()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        Assert.False(consumer.Replica.TryObserveWelcome(Encoding.UTF8.GetBytes("{\"messageType\":\"Welcome\",\"instanceId\":1,\"selfNetEntityId\":\"1\",\"connectionGeneration\":1}")));
        Assert.False(consumer.World.SelfLookup().Found);
        Assert.Equal(0, consumer.World.VisibleEntityCount);
    }

    [Fact]
    public void EmptyFullSnapshotCommitsThroughAuthorityTransaction()
    {
        ReplicaChatConsumer consumer = GameplayWireFixtures.CreateConsumer(ReplicaClientKind.Browser);
        Assert.True(GameplayWireFixtures.AdmitRoom(consumer.Replica));
        Assert.True(GameplayWireFixtures.CommitEmptySnapshot(consumer.Replica));
        ReplicaCommittedMetadata committed = consumer.Replica.GetSnapshot().Committed;
        Assert.True(committed.HasBaseline);
        Assert.Equal(10UL, committed.Baseline);
        Assert.Equal(1UL, committed.Sequence);
        Assert.True(consumer.World.SelfLookup().Found);
    }
}
