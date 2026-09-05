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
