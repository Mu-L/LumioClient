using Lumio.Client.Handshake;
using Lumio.Client.Session;
using Lumio.Client.Session.Tests.Support;

namespace Lumio.Client.Session.Tests.Fault;

public sealed class SessionAsyncHandshakeRegressionTests
{
    [Fact]
    public void CapabilityCompletionAdvancesSessionWithoutAnotherNetworkFrame()
    {
        var capability = new DeferredCapability();
        var harness = new SessionHarness(true, capability);
        harness.Connect();
        harness.Deliver(SessionTestBytes.Hello);
        harness.Tick();
        Assert.Equal(ClientSessionState.Negotiating, harness.Session.GetSnapshot().State);
        capability.Complete();
        bool completed = SpinWait.SpinUntil(() =>
        {
            harness.Tick();
            return harness.Session.GetSnapshot().State == ClientSessionState.Synchronizing;
        }, TimeSpan.FromSeconds(2));
        Assert.True(completed);
        harness.Session.RequestClose(new SessionCloseRequest(false));
    }

    [Fact]
    public void ErrorMapsToFaultPriority()
    {
        Assert.Equal(SessionEventPriority.Fault, SessionEventArbiter.MapMessage(SessionMessageKind.Error));
    }

    private sealed class DeferredCapability : IPlatformCapabilityProvider
    {
        private readonly TaskCompletionSource<PlatformCapabilityResult> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private PlatformCapabilityQuery _query;
        public ValueTask<PlatformCapabilityResult> QueryAsync(in PlatformCapabilityQuery query, CancellationToken cancellationToken)
        { _query = query; return new(_source.Task); }
        public void Complete() => _source.SetResult(new PlatformCapabilityResult(_query.Attempt, _query.Generation, true));
    }
}
