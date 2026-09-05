using System;
using System.Threading;
using Lumio.Client.Replica;

namespace Lumio.Client.Session
{
    internal sealed class AuthorityUpdateOrchestrator
    {
        private readonly bool _owned = true;

        public bool TryCommit(
            IClientReplica replica,
            IClientRuntimePort runtime,
            IClientPresentationSink presentation,
            AuthorityStageBundle bundle,
            ulong generation,
            ReadOnlyMemory<byte> update,
            ReplicaUpdateKind kind,
            ulong sequence,
            out bool baselineAck,
            out bool presented,
            out bool committed,
            out bool indeterminate)
        {
            baselineAck = false;
            presented = false;
            committed = false;
            indeterminate = false;
            if (!_owned)
            {
                return false;
            }
            ReplicaStageHandle replicaHandle;
            ReadOnlyMemory<byte> replicaPlan;
            ReplicaStageResult replicaStage = replica.StageAuthority(
                new ReplicaStageRequest(generation, kind, sequence == 0 ? 0 : sequence - 1, sequence == 0 ? 0 : sequence - 1, sequence, sequence, update, ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<ulong>.Empty),
                out replicaHandle,
                out replicaPlan);
            if (replicaStage.Status != ReplicaStageStatus.Staged)
            {
                return replicaStage.Status == ReplicaStageStatus.RequiresResync;
            }

            bundle.Replica = replicaHandle;
            bundle.ReplicaStaged = true;

            var pending = runtime.ApplyAuthoritativeTransaction(new RuntimeTransactionRequest(generation, replicaPlan), CancellationToken.None);
            RuntimeTransactionOutcome outcome = pending.IsCompleted ? pending.Result : new RuntimeTransactionOutcome(false);
            if (outcome.Indeterminate)
            {
                indeterminate = true;
                ReplicaCommittedMetadata frozen;
                replica.ObserveRuntimeOutcome(replicaHandle, ReplicaRuntimeOutcome.IndeterminateOutcome(replicaPlan), out frozen);
                bundle.Clear();
                return false;
            }

            ReplicaRuntimeOutcome replicaOutcome = outcome.Committed
                ? ReplicaRuntimeOutcome.CommittedOutcome()
                : ReplicaRuntimeOutcome.AbortedOutcome();
            ReplicaCommittedMetadata metadata;
            replica.ObserveRuntimeOutcome(replicaHandle, in replicaOutcome, out metadata);
            if (!outcome.Committed)
            {
                replica.DiscardStage(replicaHandle, ReplicaStageDiscardReason.RuntimeAborted);
                bundle.Clear();
                return false;
            }

            committed = true;
            presented = presentation.TryWrite(replicaPlan, generation).Accepted;
            bundle.Clear();
            return true;
        }
    }
}
