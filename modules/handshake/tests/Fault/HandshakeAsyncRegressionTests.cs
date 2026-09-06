using Lumio.Client.Handshake;

namespace Lumio.Client.Handshake.Tests.Fault;

public sealed class HandshakeAsyncRegressionTests
{
    [Fact]
    public void CapabilityMayCompleteAfterHandleFrameReturns()
    {
        var provider = new DeferredCapability();
        var handshake = Begin(provider, 1);
        Assert.Equal(HandshakePhase.AwaitingCapability, handshake.Poll().Phase);
        provider.Complete(1, true);
        Assert.True(handshake.Poll().Accepted);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public void DuplicateHelloDoesNotStartAnotherQuery()
    {
        var provider = new DeferredCapability();
        var handshake = Begin(provider, 1);
        handshake.HandleFrame(HandshakeTestFixtures.ServerHello);
        Assert.Equal(1, provider.Calls);
        provider.Complete(1, true);
        Assert.True(handshake.Poll().Accepted);
    }

    [Fact]
    public void CancelBeforePollCannotBecomeAccepted()
    {
        var provider = new DeferredCapability();
        var handshake = Begin(provider, 1);
        provider.Complete(1, true);
        handshake.Cancel();
        Assert.Equal(HandshakePhase.Cancelled, handshake.Poll().Phase);
    }

    [Fact]
    public void OldCompletionCannotSatisfyNewAttempt()
    {
        var provider = new DeferredCapability();
        var handshake = Begin(provider, 1);
        TaskCompletionSource<PlatformCapabilityResult> old = provider.Pending;
        CancellationToken oldToken = provider.Token;
        handshake.Cancel();
        Assert.True(oldToken.IsCancellationRequested);
        handshake.Begin(new HandshakeBeginRequest(new HandshakeAttemptId(2), 2));
        handshake.HandleFrame(HandshakeTestFixtures.ServerHello);
        old.SetResult(new PlatformCapabilityResult(new HandshakeAttemptId(1), 1, true));
        Assert.Equal(HandshakePhase.AwaitingCapability, handshake.Poll().Phase);
        provider.Complete(2, true);
        Assert.True(handshake.Poll().Accepted);
    }

    [Fact]
    public void DelayedProviderFaultRejectsInsteadOfHanging()
    {
        var provider = new DeferredCapability();
        var handshake = Begin(provider, 1);
        provider.Pending.SetException(new InvalidOperationException("capability failed"));
        Assert.Equal(HandshakePhase.Rejected, handshake.Poll().Phase);
    }

    [Fact]
    public void WrongGenerationResultIsRejected()
    {
        var provider = new DeferredCapability();
        var handshake = Begin(provider, 1);
        provider.Complete(2, true);
        Assert.Equal(HandshakePhase.Rejected, handshake.Poll().Phase);
    }

    private static IClientHandshake Begin(DeferredCapability provider, ulong generation)
    {
        IClientHandshake handshake = new ClientHandshakeFactory().Create(provider, HandshakeTestFixtures.Classifier);
        handshake.Begin(new HandshakeBeginRequest(new HandshakeAttemptId(generation), generation));
        handshake.HandleFrame(HandshakeTestFixtures.ServerHello);
        return handshake;
    }

    private sealed class DeferredCapability : IPlatformCapabilityProvider
    {
        // Deliberately allow inline continuations so Poll assertions are deterministic
        // without sleeps; the production continuation only completes a Task.
        public TaskCompletionSource<PlatformCapabilityResult> Pending { get; private set; } = new();
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }

        public ValueTask<PlatformCapabilityResult> QueryAsync(in PlatformCapabilityQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            Token = cancellationToken;
            Pending = new TaskCompletionSource<PlatformCapabilityResult>();
            return new ValueTask<PlatformCapabilityResult>(Pending.Task);
        }

        public void Complete(ulong generation, bool compatible)
        {
            Pending.SetResult(new PlatformCapabilityResult(new HandshakeAttemptId(generation), generation, compatible));
        }
    }
}
