using Lumio.Client.Replica;
using Lumio.Client.Session;

namespace Lumio.Client.Session.Tests.Fault;

public sealed class AuthorityCommitRegressionTests
{
    [Fact]
    public void SuccessWithoutApplyingAStageIsIndeterminateAndNeverPresented()
    {
        var replica = new ReplicaDouble();
        var presentation = new NullPresentationSink();
        var owner = new AuthorityUpdateOrchestrator();
        owner.TryCommit(replica, new LyingRuntime(), presentation, new AuthorityStageBundle(), 1,
            new byte[] { 1 }, ReplicaUpdateKind.FullSnapshot, 1,
            out _, out _, out bool committed, out bool indeterminate);
        Assert.False(committed);
        Assert.True(indeterminate);
        Assert.Equal(0, replica.Applies);
        Assert.Equal(0, presentation.WriteCalls);
    }

    [Fact]
    public void ReplicaRejectionCannotBePromotedToCommitted()
    {
        var replica = new ReplicaDouble { Reject = true };
        var presentation = new NullPresentationSink();
        var owner = new AuthorityUpdateOrchestrator();
        owner.TryCommit(replica, new WorldChangeRuntimePort(), presentation, new AuthorityStageBundle(), 1,
            new byte[] { 1 }, ReplicaUpdateKind.FullSnapshot, 1,
            out _, out _, out bool committed, out bool indeterminate);
        Assert.False(committed);
        Assert.True(indeterminate);
        Assert.Equal(0, presentation.WriteCalls);
    }

    [Fact]
    public void PendingRemainsPendingAndCompletedStageIsAppliedExactlyOnce()
    {
        var replica = new ReplicaDouble();
        var runtime = new DelayedRuntime();
        var presentation = new NullPresentationSink();
        var owner = new AuthorityUpdateOrchestrator();
        owner.TryCommit(replica, runtime, presentation, new AuthorityStageBundle(), 1,
            new byte[] { 1 }, ReplicaUpdateKind.FullSnapshot, 1,
            out _, out _, out bool committed, out bool indeterminate);
        Assert.True(owner.IsPending);
        Assert.False(committed);
        Assert.False(indeterminate);
        Assert.Equal(0, replica.Discards);
        Assert.Equal(0, replica.Applies);
        runtime.CompleteOnOwner();
        owner.TryComplete(out _, out _, out committed, out indeterminate);
        Assert.True(committed);
        Assert.False(indeterminate);
        Assert.False(owner.IsPending);
        Assert.Equal(1, replica.Applies);
        Assert.Equal(1, presentation.WriteCalls);
        owner.TryComplete(out _, out _, out committed, out _);
        Assert.False(committed);
        Assert.Equal(1, replica.Applies);
    }

    [Fact]
    public void CloseInvalidatesCapturedRequestBeforeLateCompletion()
    {
        var replica = new ReplicaDouble();
        var runtime = new DelayedRuntime();
        var owner = new AuthorityUpdateOrchestrator();
        owner.TryCommit(replica, runtime, new NullPresentationSink(), new AuthorityStageBundle(), 1,
            new byte[] { 1 }, ReplicaUpdateKind.FullSnapshot, 1, out _, out _, out _, out _);
        owner.CancelPending();
        runtime.CompleteOnOwner();
        Assert.False(owner.IsPending);
        Assert.Equal(0, replica.Applies);
        Assert.Equal(1, replica.Discards);
    }

    [Fact]
    public void PresentationExceptionDoesNotRetryACommittedWorldChange()
    {
        var replica = new ReplicaDouble();
        var owner = new AuthorityUpdateOrchestrator();
        owner.TryCommit(replica, new WorldChangeRuntimePort(), new ThrowingPresentation(), new AuthorityStageBundle(), 1,
            new byte[] { 1 }, ReplicaUpdateKind.FullSnapshot, 1,
            out _, out bool presented, out bool committed, out bool indeterminate);
        Assert.True(committed);
        Assert.False(presented);
        Assert.False(indeterminate);
        Assert.Equal(1, replica.Applies);
    }

    [Fact]
    public void RuntimeFaultDoesNotTurnIntoAnOrdinaryAbort()
    {
        var replica = new ReplicaDouble();
        var runtime = new DelayedRuntime();
        var owner = new AuthorityUpdateOrchestrator();
        owner.TryCommit(replica, runtime, new NullPresentationSink(), new AuthorityStageBundle(), 1,
            new byte[] { 1 }, ReplicaUpdateKind.FullSnapshot, 1, out _, out _, out _, out _);
        runtime.Fail();
        owner.TryComplete(out _, out _, out bool committed, out bool indeterminate);
        Assert.False(committed);
        Assert.True(indeterminate);
        Assert.Equal(0, replica.Applies);
    }

    [Fact]
    public void CommitOnAnotherThreadIsRejectedBeforeWorldMutation()
    {
        var replica = new ReplicaDouble();
        var commit = new RuntimeAuthorityCommit(replica, new ReplicaStageHandle(1, 1), 1, 1);
        RuntimeTransactionOutcome result = default;
        var worker = new Thread(() => result = commit.Apply());
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.True(result.Indeterminate);
        Assert.Equal(0, replica.Applies);
    }

    private sealed class LyingRuntime : IClientRuntimePort
    {
        public ValueTask<RuntimeTransactionOutcome> ApplyAuthoritativeTransaction(in RuntimeTransactionRequest request, CancellationToken cancellationToken)
            => new(new RuntimeTransactionOutcome(true));
        public ValueTask<RuntimeTransactionOutcome> ApplyLocalPrediction(in RuntimeTransactionRequest request, CancellationToken cancellationToken)
            => new(new RuntimeTransactionOutcome(false));
    }

    private sealed class DelayedRuntime : IClientRuntimePort
    {
        private readonly TaskCompletionSource<RuntimeTransactionOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private RuntimeTransactionRequest _request;
        public ValueTask<RuntimeTransactionOutcome> ApplyAuthoritativeTransaction(in RuntimeTransactionRequest request, CancellationToken cancellationToken)
        {
            _request = request;
            return new(_completion.Task);
        }
        public ValueTask<RuntimeTransactionOutcome> ApplyLocalPrediction(in RuntimeTransactionRequest request, CancellationToken cancellationToken)
            => new(new RuntimeTransactionOutcome(false));
        public void CompleteOnOwner() => _completion.SetResult(_request.CommitAuthority());
        public void Fail() => _completion.SetException(new InvalidOperationException("injected runtime failure"));
    }

    private sealed class ThrowingPresentation : IClientPresentationSink
    {
        public PresentationWriteResult TryWrite(ReadOnlyMemory<byte> committedDiff, ulong sessionGeneration)
            => throw new InvalidOperationException("injected presentation failure");
    }

    private sealed class ReplicaDouble : IClientReplica
    {
        private ReplicaCommittedMetadata _metadata = new(1, 0, 0, 0, false, false);
        public bool Reject { get; init; }
        public int Applies { get; private set; }
        public int Discards { get; private set; }
        public IReplicaWorld World => throw new NotSupportedException();
        public ReplicaStageResult StageAuthority(in ReplicaStageRequest request, out ReplicaStageHandle stageHandle, out ReadOnlyMemory<byte> applyPlan)
        { stageHandle = new(1, 1); applyPlan = new byte[] { 1 }; return new(ReplicaStageStatus.Staged); }
        public ReplicaOutcomeStatus ObserveRuntimeOutcome(ReplicaStageHandle stageHandle, in ReplicaRuntimeOutcome outcome, out ReplicaCommittedMetadata committedMetadata)
        {
            if (outcome.Committed)
            {
                Applies++;
                if (Reject) { committedMetadata = _metadata; return ReplicaOutcomeStatus.Rejected; }
                _metadata = new(1, 1, 1, 1, true, false);
            }
            committedMetadata = _metadata;
            return outcome.Indeterminate ? ReplicaOutcomeStatus.Frozen : ReplicaOutcomeStatus.Observed;
        }
        public ReplicaOutcomeStatus DiscardStage(ReplicaStageHandle handle, ReplicaStageDiscardReason reason)
        { Discards++; return ReplicaOutcomeStatus.Discarded; }
        public ReplicaResetResult ResetForNewSession(in ReplicaResetRequest request) => new(true);
        public bool TryObserveWelcome(ReadOnlyMemory<byte> utf8) => true;
        public bool TryObserveConnectionSuperseded(ReadOnlyMemory<byte> utf8, out ReplicaConnectionSuperseded notice)
        { notice = default; return false; }
        public ReplicaSnapshot GetSnapshot() => new(_metadata, 0, ReplicaStageStatus.None, ReadOnlyMemory<byte>.Empty);
    }
}
