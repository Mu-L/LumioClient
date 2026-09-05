using Lumio.Client.Connection;
using Lumio.Client.Session;
using Lumio.Client.Session.Tests.Support;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Samples.Username.Components.Chat;

namespace Lumio.Client.Session.Tests.Unit;

public sealed class SessionPublicApiTests
{
    [Fact]
    public void RuntimeHandleLedger_AndFirstConnectHappyPath()
    {
        var harness = new SessionHarness(true);
        harness.HappyPathToActive();
        ClientSessionSnapshot snapshot = harness.Session.GetSnapshot();
        Assert.Equal(ClientSessionState.Active, snapshot.State);
        Assert.True(snapshot.RuntimeCommitted);
        Assert.True(snapshot.LedgerCount > 0);
        harness.Session.RequestClose(new SessionCloseRequest(false));
        Assert.Equal(ClientSessionState.Closed, harness.Session.GetSnapshot().State);
    }

    [Fact]
    public void AuthorityTransactionFault_DoesNotCommit()
    {
        var harness = new SessionHarness(false);
        harness.Connect();
        harness.Tick();
        harness.Deliver(SessionTestBytes.Hello);
        harness.Tick();
        harness.Deliver(SessionTestBytes.Snapshot);
        harness.Tick();
        Assert.NotEqual(ClientSessionState.Active, harness.Session.GetSnapshot().State);
        Assert.False(harness.Session.GetSnapshot().RuntimeCommitted);
    }

    [Fact]
    public void ServerWelcomeStartsGameplaySynchronizationWithoutLegacyBaselineAck()
    {
        var harness = new SessionHarness(true);
        var endpoint = new ClientEndpoint(
            "ws://127.0.0.1:1",
            new byte[] { 1 },
            new byte[] { 2 },
            TimeSpan.FromSeconds(1),
            new byte[] { 3 });
        Assert.True(harness.Session.RequestConnect(
            new SessionConnectRequest(1, endpoint),
            CancellationToken.None).Succeeded);
        harness.Tick();
        harness.Deliver(SessionTestBytes.Welcome);
        harness.Tick();
        Assert.Equal(ClientSessionState.Synchronizing, harness.Session.GetSnapshot().State);
        Assert.True(harness.Session.TryGetReplicaWorld(out var synchronizedWorld));
        Assert.True(synchronizedWorld.SelfLookup().Found);
        Assert.Equal(new NetEntityId(7UL, 2UL).ToHex(), synchronizedWorld.SelfLookup().Binding.NetEntityId);

        harness.Deliver(SessionTestBytes.WorldChange);
        harness.Tick();

        Assert.Equal(ClientSessionState.Active, harness.Session.GetSnapshot().State);
        Assert.True(harness.Session.GetSnapshot().RuntimeCommitted);
        Assert.False(harness.Session.GetSnapshot().BaselineAckSent);
        Assert.False(harness.Connections.Loopback.TryReceiveFromClient(out _));

        Assert.True(harness.Session.TryGetReplicaWorld(out var world));
        world.Manager.World.Self.Get<ChatComponent>().SendMessage("hello-runtime");
        world.Manager.Tick();
        harness.Tick();

        Assert.True(harness.Connections.Loopback.TryReceiveFromClient(out EncodedFrame outbound));
        InputCommandMessage decoded = WireCodec.DecodeInput(outbound.Bytes.Span);
        Assert.Equal(WireCodec.ChatInput, decoded.MappingId);
    }

    [Fact]
    public void WelcomeWithWrongConnectionGenerationIsRejectedBeforeSynchronization()
    {
        var harness = new SessionHarness(true);
        harness.Connect(1UL);
        harness.Tick();
        harness.Deliver(WireCodec.EncodePack(new WelcomeMessage(7UL, new NetEntityId(7UL, 2UL), 2UL)));
        harness.Tick();

        Assert.Equal(ClientSessionState.Faulted, harness.Session.GetSnapshot().State);
        Assert.False(harness.Session.GetSnapshot().RuntimeCommitted);
    }

    [Fact]
    public void OutboundObserverReceivesTypedInputAndEncodedWireBytes()
    {
        var observer = new RecordingOutboundObserver();
        var harness = new SessionHarness(true, observer);
        harness.HappyPathToActive();

        Assert.True(harness.Session.TryGetReplicaWorld(out var world));
        world.Manager.World.Self.Get<ChatComponent>().SendMessage("hello-observer");
        world.Manager.Tick();
        harness.Tick();

        var observed = Assert.Single(observer.Records);
        InputCommandMessage decoded = WireCodec.DecodeInput(observed.EncodedBytes);
        Assert.Equal(WireCodec.ChatInput, decoded.MappingId);
        Assert.Equal(observed.Message.Payload.ToArray(), decoded.Payload.ToArray());
    }

    [Fact]
    public void OutboundFalseThenTrueRetriesIdenticalBytesAndObservesOnce()
    {
        var observer = new RecordingOutboundObserver();
        var harness = new SessionHarness(true, observer);
        harness.HappyPathToActive();
        harness.Connections.SendAttempts.Clear();
        harness.Connections.QueueSendResults(false, true);

        Assert.True(harness.Session.TryGetReplicaWorld(out var world));
        world.Manager.World.Self.Get<ChatComponent>().SendMessage("retry-once");
        world.Manager.Tick();

        harness.Tick();
        Assert.Empty(observer.Records);
        harness.Tick();

        Assert.Single(observer.Records);
        Assert.Equal(2, harness.Connections.SendAttempts.Count);
        Assert.Equal(harness.Connections.SendAttempts[0], harness.Connections.SendAttempts[1]);
    }

    [Fact]
    public void OutboundPendingInputsRemainOrderedAcrossBackpressure()
    {
        var observer = new RecordingOutboundObserver();
        var harness = new SessionHarness(true, observer);
        harness.HappyPathToActive();
        harness.Connections.SendAttempts.Clear();
        harness.Connections.QueueSendResults(false, true, true);

        Assert.True(harness.Session.TryGetReplicaWorld(out var world));
        ChatComponent chat = world.Manager.World.Self.Get<ChatComponent>();
        chat.SendMessage("retry-first");
        chat.SendMessage("retry-second");
        world.Manager.Tick();

        harness.Tick();
        Assert.Empty(observer.Records);
        harness.Tick();

        Assert.Equal(2, observer.Records.Count);
        Assert.Equal(1UL, observer.Records[0].Message.Sequence);
        Assert.Equal(2UL, observer.Records[1].Message.Sequence);
        Assert.Equal(3, harness.Connections.SendAttempts.Count);
        Assert.Equal(harness.Connections.SendAttempts[0], harness.Connections.SendAttempts[1]);
        Assert.Equal(observer.Records[0].EncodedBytes, harness.Connections.SendAttempts[1]);
        Assert.Equal(observer.Records[1].EncodedBytes, harness.Connections.SendAttempts[2]);
    }

    [Fact]
    public void OutboundPendingQueueOverflowFaultsWithoutSilentDrop()
    {
        var observer = new RecordingOutboundObserver();
        var harness = new SessionHarness(true, observer);
        harness.HappyPathToActive();
        harness.Connections.SendAttempts.Clear();
        harness.Connections.QueueSendResults(Enumerable.Repeat(false, 65).ToArray());

        Assert.True(harness.Session.TryGetReplicaWorld(out var world));
        ChatComponent chat = world.Manager.World.Self.Get<ChatComponent>();
        for (int i = 0; i < 65; i++)
        {
            chat.SendMessage("overflow-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        world.Manager.Tick();
        harness.Tick();

        Assert.Equal(ClientSessionState.Faulted, harness.Session.GetSnapshot().State);
        Assert.Empty(observer.Records);
        Assert.Equal(1, harness.Connections.CloseCount);
        Assert.Equal(0, harness.Session.GetSnapshot().LedgerCount);
    }

    private sealed class RecordingOutboundObserver : IClientOutboundMessageObserver
    {
        public List<OutboundRecord> Records { get; } = new();

        public void Observe(InputCommandMessage message, ReadOnlyMemory<byte> encodedBytes)
        {
            Records.Add(new OutboundRecord(message, encodedBytes.ToArray()));
        }
    }

    private readonly record struct OutboundRecord(InputCommandMessage Message, byte[] EncodedBytes);
}
