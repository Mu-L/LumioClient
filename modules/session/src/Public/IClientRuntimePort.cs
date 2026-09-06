using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lumio.Client.Session
{
    public readonly struct RuntimeTransactionRequest
    {
        private readonly RuntimeAuthorityCommit? _authorityCommit;

        public RuntimeTransactionRequest(ulong generation, ReadOnlyMemory<byte> opaquePlan)
        {
            Generation = generation;
            OpaquePlan = opaquePlan;
            _authorityCommit = null;
        }

        internal RuntimeTransactionRequest(ulong generation, ReadOnlyMemory<byte> opaquePlan, RuntimeAuthorityCommit authorityCommit)
        {
            Generation = generation;
            OpaquePlan = opaquePlan;
            _authorityCommit = authorityCommit;
        }

        public ulong Generation { get; }
        public ReadOnlyMemory<byte> OpaquePlan { get; }

        /// <summary>
        /// Apply the staged WorldChange on its owner thread. A successful authority
        /// port must call this exactly once before reporting Committed. The request
        /// becomes unusable when its generation is closed. It is not a wire type.
        /// </summary>
        public RuntimeTransactionOutcome CommitAuthority()
            => _authorityCommit == null ? new RuntimeTransactionOutcome(false) : _authorityCommit.Apply();
    }

    public readonly struct RuntimeTransactionOutcome
    {
        public RuntimeTransactionOutcome(bool committed) : this(committed, false) { }

        public RuntimeTransactionOutcome(bool committed, bool indeterminate)
        {
            Committed = committed && !indeterminate;
            Indeterminate = indeterminate;
        }

        public bool Committed { get; }
        public bool Indeterminate { get; }
        public static RuntimeTransactionOutcome IndeterminateOutcome() => new RuntimeTransactionOutcome(false, true);
    }

    public interface IClientRuntimePort
    {
        ValueTask<RuntimeTransactionOutcome> ApplyAuthoritativeTransaction(
            in RuntimeTransactionRequest request, CancellationToken cancellationToken);
        ValueTask<RuntimeTransactionOutcome> ApplyLocalPrediction(
            in RuntimeTransactionRequest request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Runtime-backed WorldChange adapter. The staged replica invokes its real
    /// WorldManager, and metadata advances only if that application succeeds.
    /// This adapter does not implement GAS/Voxel atomicity or local prediction.
    /// </summary>
    public sealed class WorldChangeRuntimePort : IClientRuntimePort
    {
        public ValueTask<RuntimeTransactionOutcome> ApplyAuthoritativeTransaction(
            in RuntimeTransactionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<RuntimeTransactionOutcome>(request.CommitAuthority());
        }

        public ValueTask<RuntimeTransactionOutcome> ApplyLocalPrediction(
            in RuntimeTransactionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<RuntimeTransactionOutcome>(new RuntimeTransactionOutcome(false));
        }
    }
}
