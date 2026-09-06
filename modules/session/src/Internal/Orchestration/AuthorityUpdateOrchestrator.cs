using System;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Client.Replica;

namespace Lumio.Client.Session
{
    internal sealed class AuthorityUpdateOrchestrator : IDisposable
    {
        private Task<RuntimeTransactionOutcome>? _pending;
        private CancellationTokenSource? _cancellation;
        private RuntimeAuthorityCommit? _commit;
        private IClientReplica? _replica;
        private IClientPresentationSink? _presentation;
        private AuthorityStageBundle? _bundle;
        private ReplicaStageHandle _handle;
        private ReadOnlyMemory<byte> _plan;
        private ulong _generation;

        public bool IsPending => _pending != null;
        public ReplicaStageStatus LastStageStatus { get; private set; }

        public bool TryCommit(IClientReplica replica, IClientRuntimePort runtime,
            IClientPresentationSink presentation, AuthorityStageBundle bundle,
            ulong generation, ReadOnlyMemory<byte> update, ReplicaUpdateKind kind, ulong sequence,
            out bool baselineAck, out bool presented, out bool committed, out bool indeterminate)
        {
            baselineAck = presented = committed = indeterminate = false;
            if (IsPending) return false;
            ReplicaCommittedMetadata previous = replica.GetSnapshot().Committed;
            var request = new ReplicaStageRequest(generation, kind, previous.Baseline,
                previous.Revision, sequence, sequence, update,
                ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<ulong>.Empty);
            ReplicaStageResult staged = replica.StageAuthority(in request, out _handle, out ReadOnlyMemory<byte> plan);
            LastStageStatus = staged.Status;
            if (staged.Status != ReplicaStageStatus.Staged)
                return staged.Status == ReplicaStageStatus.RequiresResync;

            _replica = replica;
            _presentation = presentation;
            _bundle = bundle;
            _generation = generation;
            _plan = plan.ToArray();
            bundle.Replica = _handle;
            bundle.ReplicaStaged = true;
            _commit = new RuntimeAuthorityCommit(replica, _handle, generation, sequence);
            _cancellation = new CancellationTokenSource();
            try
            {
                // Convert once; never interpret an incomplete ValueTask as Abort.
                _pending = runtime.ApplyAuthoritativeTransaction(
                    new RuntimeTransactionRequest(generation, _plan, _commit), _cancellation.Token).AsTask();
            }
            catch (Exception)
            {
                _pending = Task.FromResult(RuntimeTransactionOutcome.IndeterminateOutcome());
            }
            return TryComplete(out baselineAck, out presented, out committed, out indeterminate);
        }

        public bool TryComplete(out bool baselineAck, out bool presented, out bool committed, out bool indeterminate)
        {
            baselineAck = presented = committed = indeterminate = false;
            if (_pending == null || !_pending.IsCompleted) return false;
            RuntimeTransactionOutcome outcome;
            try { outcome = _commit!.Reconcile(_pending.GetAwaiter().GetResult()); }
            catch (Exception) { outcome = RuntimeTransactionOutcome.IndeterminateOutcome(); }
            indeterminate = outcome.Indeterminate;
            committed = outcome.Committed;
            if (!committed)
            {
                if (indeterminate)
                    _replica!.ObserveRuntimeOutcome(_handle, ReplicaRuntimeOutcome.IndeterminateOutcome(_plan), out _);
                else
                    _replica!.DiscardStage(_handle, ReplicaStageDiscardReason.RuntimeAborted);
            }
            else
            {
                // Presentation is downstream of the commit. A sink failure must not
                // retry the authority transaction or erase the committed receipt.
                try { presented = _presentation!.TryWrite(_plan, _generation).Accepted; }
                catch (Exception) { presented = false; }
            }
            Clear();
            return committed;
        }

        public void CancelPending()
        {
            _commit?.Abandon();
            if (_cancellation != null)
            {
                try { _cancellation.Cancel(); }
                catch (AggregateException) { /* Callback failure cannot restore the stage. */ }
            }
            if (_pending != null) ObserveAbandoned(_pending);
            if (_replica != null) _replica.DiscardStage(_handle, ReplicaStageDiscardReason.SessionReset);
            Clear();
        }

        public void Dispose()
        {
            // Releases the runtime cancellation source of any pending commit.
            // Idempotent: a second call finds nothing pending and no source.
            // The orchestrator stays reusable; the next TryCommit allocates anew.
            CancelPending();
        }

        private static void ObserveAbandoned(Task<RuntimeTransactionOutcome> task)
        {
            _ = task.ContinueWith(static completed => { _ = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void Clear()
        {
            _bundle?.Clear();
            _commit?.Abandon();
            _pending = null;
            _commit = null;
            _replica = null;
            _presentation = null;
            _bundle = null;
            _plan = ReadOnlyMemory<byte>.Empty;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }
}
