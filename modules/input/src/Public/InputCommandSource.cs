using System;

namespace Lumio.Client.Input
{
    public sealed class InputCommandSource : IInputCommandSource
    {
        private readonly IInputSampleIngress _ingress;
        private readonly IGameInputMapper _mapper;
        private readonly InputBufferPolicyState _policy = new InputBufferPolicyState();
        private SequencedInputSample[] _pending = Array.Empty<SequencedInputSample>();
        private int _pendingOffset;
        private ulong _pendingGeneration;

        public InputCommandSource(IInputSampleIngress ingress, IGameInputMapper mapper)
        {
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        }

        public int DrainCandidates(Span<GameplayCommandCandidate> destination, in InputDrainContext context)
        {
            if (_pendingGeneration != context.Generation)
            {
                ClearPending();
                _pendingGeneration = context.Generation;
            }

            if (_policy.AppliesTo(context.Generation))
            {
                if (_policy.Current.Kind == InputBufferPolicyKind.Drop)
                {
                    ClearPending();
                    _ingress.DrainAccepted();
                    return 0;
                }

                if (_policy.Current.Kind == InputBufferPolicyKind.Resync)
                {
                    return 0;
                }
            }

            int limit = Math.Min(destination.Length, context.MaxCandidates);
            if (limit <= 0)
            {
                return 0;
            }

            // Retain at most one ingress batch. Never drain another batch until
            // this one is consumed: a per-tick output budget must not lose input.
            if (_pendingOffset == _pending.Length)
            {
                _pending = _ingress.DrainAccepted();
                _pendingOffset = 0;
            }

            int written = 0;
            while (_pendingOffset < _pending.Length && written < limit)
            {
                SequencedInputSample sample = _pending[_pendingOffset++];
                try
                {
                    if (_mapper.TryMap(in sample, in context, out GameplayCommandCandidate candidate)
                        && !candidate.ClientCommandSeq.HasValue)
                    {
                        destination[written++] = candidate;
                    }
                }
                catch (Exception)
                {
                    // A mapper failure rejects this sample, not the remaining batch.
                }
            }

            if (_pendingOffset == _pending.Length)
            {
                ClearPending();
            }

            return written;
        }

        public void SetBufferPolicy(in InputBufferPolicy policy)
        {
            _policy.Set(in policy);
            if (policy.Kind == InputBufferPolicyKind.Drop && policy.Generation == _pendingGeneration)
            {
                ClearPending();
            }
        }

        public InputBufferPolicy GetSnapshotPolicy()
        {
            return _policy.Current;
        }

        private void ClearPending()
        {
            _pending = Array.Empty<SequencedInputSample>();
            _pendingOffset = 0;
        }
    }
}
