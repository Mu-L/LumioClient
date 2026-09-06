using Lumio.Client.Session;
using Lumio.Client.Session.Tests.Support;

namespace Lumio.Client.Session.Tests.Fault;

public sealed class SessionGenerationRegressionTests
{
    [Fact]
    public void DisconnectAndOldWelcomeInOneBatchDoNotFaultTheNewGeneration()
    {
        var harness = new SessionHarness(true);
        harness.Connect();
        harness.Tick();
        harness.Deliver(SessionTestBytes.Welcome);
        harness.Connections.DisconnectAfterFrameDrain = true;
        harness.Tick();
        Assert.Equal(2UL, harness.Session.GetSnapshot().Generation);
        Assert.Equal(ClientSessionState.Negotiating, harness.Session.GetSnapshot().State);
        Assert.Equal(0, harness.Runtime.AuthorityCalls);
        harness.Session.RequestClose(new SessionCloseRequest(false));
    }

    [Fact]
    public void PresentationCloseDoesNotResurrectSessionOrDisposeInsideTheCallback()
    {
        SessionHarness? harness = null;
        bool worldAvailableDuringCallback = false;
        var sink = new ClosingPresentation(() =>
        {
            harness!.Session.RequestClose(new SessionCloseRequest(false));
            worldAvailableDuringCallback = harness.Session.TryGetReplicaWorld(out _);
        });
        harness = new SessionHarness(true, sink);
        harness.HappyPathToActive();
        Assert.True(worldAvailableDuringCallback);
        Assert.Equal(ClientSessionState.Closed, harness.Session.GetSnapshot().State);
        Assert.False(harness.Session.TryGetReplicaWorld(out _));
    }

    private sealed class ClosingPresentation : IClientPresentationSink
    {
        private readonly Action _close;
        public ClosingPresentation(Action close) { _close = close; }
        public PresentationWriteResult TryWrite(ReadOnlyMemory<byte> committedDiff, ulong sessionGeneration)
        { _close(); return new PresentationWriteResult(true); }
    }

    [Fact]
    public void CloseIsIdempotentAndDoesNotLeaveAUsableReplica()
    {
        var harness = new SessionHarness(true);
        harness.Connect();
        harness.Session.RequestClose(new SessionCloseRequest(false));
        harness.Session.RequestClose(new SessionCloseRequest(false));
        harness.Tick();
        Assert.Equal(ClientSessionState.Closed, harness.Session.GetSnapshot().State);
        Assert.False(harness.Session.TryGetReplicaWorld(out _));
        Assert.Equal(1, harness.Connections.CloseCount);
    }
}
