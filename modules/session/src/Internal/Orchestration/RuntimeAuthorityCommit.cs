using System;
using System.Threading;
using Lumio.Client.Replica;

namespace Lumio.Client.Session
{
    // One-use, owner-thread commit receipt shared by every copy of a request.
    // A provider returning true without applying the actual stage cannot forge it.
    internal sealed class RuntimeAuthorityCommit
    {
        private readonly object _gate = new object();
        private readonly IClientReplica _replica;
        private readonly ReplicaStageHandle _stage;
        private readonly ulong _generation;
        private readonly ulong _sequence;
        private readonly int _ownerThread = Environment.CurrentManagedThreadId;
        private bool _abandoned;
        private bool _attempted;
        private RuntimeTransactionOutcome _outcome;

        public RuntimeAuthorityCommit(IClientReplica replica, ReplicaStageHandle stage, ulong generation, ulong sequence)
        {
            _replica = replica;
            _stage = stage;
            _generation = generation;
            _sequence = sequence;
        }

        public RuntimeTransactionOutcome Apply()
        {
            lock (_gate)
            {
                if (_abandoned) return new RuntimeTransactionOutcome(false);
                if (_attempted) return _outcome;
                _attempted = true;
                if (Environment.CurrentManagedThreadId != _ownerThread
                    || _replica.GetSnapshot().Committed.Generation != _generation)
                {
                    _outcome = RuntimeTransactionOutcome.IndeterminateOutcome();
                    return _outcome;
                }
                try
                {
                    ReplicaOutcomeStatus status = _replica.ObserveRuntimeOutcome(
                        _stage, ReplicaRuntimeOutcome.CommittedOutcome(), out ReplicaCommittedMetadata metadata);
                    bool applied = status == ReplicaOutcomeStatus.Observed
                        && !metadata.Frozen && metadata.HasBaseline
                        && metadata.Generation == _generation && metadata.Sequence == _sequence;
                    // A rejected application may already have entered WorldManager.Tick.
                    // Without an explicit rollback receipt it is not safe to call it Abort.
                    _outcome = applied ? new RuntimeTransactionOutcome(true)
                        : RuntimeTransactionOutcome.IndeterminateOutcome();
                }
                catch (Exception)
                {
                    _outcome = RuntimeTransactionOutcome.IndeterminateOutcome();
                }
                return _outcome;
            }
        }

        public RuntimeTransactionOutcome Reconcile(RuntimeTransactionOutcome reported)
        {
            lock (_gate)
            {
                if (_abandoned || reported.Indeterminate || (_attempted && _outcome.Indeterminate))
                    return RuntimeTransactionOutcome.IndeterminateOutcome();
                if (reported.Committed != (_attempted && _outcome.Committed))
                    return RuntimeTransactionOutcome.IndeterminateOutcome();
                return reported;
            }
        }

        public void Abandon()
        {
            lock (_gate) { _abandoned = true; }
        }
    }
}
