using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lumio.Client.Handshake
{
    internal sealed class HandshakeSession : IClientHandshake, IDisposable
    {
        private readonly IPlatformCapabilityProvider _capabilities;
        private readonly IHandshakeFrameClassifier _classifier;
        private HandshakeAttemptId _attempt;
        private ulong _generation;
        private HandshakePhase _phase = HandshakePhase.Idle;
        private HandshakeRejectReason _reject;
        private Task<PlatformCapabilityResult>? _pendingCapability;
        private CancellationTokenSource? _capabilityCancellation;

        public HandshakeSession(IPlatformCapabilityProvider capabilities)
            : this(capabilities, new UnpublishedHandshakeFrameClassifier())
        {
        }

        public HandshakeSession(IPlatformCapabilityProvider capabilities, IHandshakeFrameClassifier classifier)
        {
            _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
            _classifier = classifier ?? new UnpublishedHandshakeFrameClassifier();
        }

        public HandshakeCommandResult Begin(in HandshakeBeginRequest request)
        {
            if (_phase != HandshakePhase.Idle
                && _phase != HandshakePhase.Rejected
                && _phase != HandshakePhase.Cancelled
                && _phase != HandshakePhase.Accepted)
            {
                return new HandshakeCommandResult(false);
            }

            CancelPendingCapability();
            _attempt = request.Attempt;
            _generation = request.Generation;
            _phase = HandshakePhase.AwaitingHello;
            _reject = HandshakeRejectReason.None;
            return new HandshakeCommandResult(true);
        }

        public HandshakeCommandResult HandleFrame(ReadOnlyMemory<byte> frame)
        {
            if (_phase != HandshakePhase.AwaitingHello && _phase != HandshakePhase.AwaitingCapability)
            {
                return new HandshakeCommandResult(false);
            }

            HandshakeOpaqueFrameRole role = frame.IsEmpty
                ? HandshakeOpaqueFrameRole.Unclassified
                : _classifier.Classify(frame);
            if (role == HandshakeOpaqueFrameRole.Unclassified)
            {
                return new HandshakeCommandResult(false);
            }

            if (role == HandshakeOpaqueFrameRole.HandshakeReject)
            {
                _phase = HandshakePhase.Rejected;
                _reject = HandshakeRejectReason.InvalidHello;
                CancelPendingCapability();
                return new HandshakeCommandResult(true);
            }

            if (role != HandshakeOpaqueFrameRole.ServerHello || _phase != HandshakePhase.AwaitingHello)
            {
                return new HandshakeCommandResult(false);
            }

            _phase = HandshakePhase.AwaitingCapability;
            _capabilityCancellation = new CancellationTokenSource();
            // Consume the ValueTask exactly once. The owner polls the resulting
            // Task; a continuation never mutates session state on a worker thread.
            _pendingCapability = QueryCapabilityAsync(
                new PlatformCapabilityQuery(_attempt, _generation),
                _capabilityCancellation.Token);
            return new HandshakeCommandResult(true);
        }

        public HandshakeOutcome Poll()
        {
            if (_phase != HandshakePhase.AwaitingCapability
                || _pendingCapability == null
                || !_pendingCapability.IsCompleted)
            {
                return GetSnapshot();
            }

            PlatformCapabilityResult result = _pendingCapability.GetAwaiter().GetResult();
            _pendingCapability = null;
            _capabilityCancellation?.Dispose();
            _capabilityCancellation = null;
            if (result.Attempt.Value != _attempt.Value || result.Generation != _generation || !result.Compatible)
            {
                _phase = HandshakePhase.Rejected;
                _reject = HandshakeRejectReason.CapabilityMismatch;
            }
            else
            {
                _phase = HandshakePhase.Accepted;
            }

            return GetSnapshot();
        }

        public HandshakeCommandResult Cancel()
        {
            if (_phase == HandshakePhase.Accepted)
            {
                return new HandshakeCommandResult(false);
            }

            if (_phase != HandshakePhase.Idle)
            {
                _phase = HandshakePhase.Cancelled;
                _reject = HandshakeRejectReason.Cancelled;
            }

            CancelPendingCapability();
            return new HandshakeCommandResult(true);
        }

        public HandshakeOutcome GetSnapshot()
        {
            return new HandshakeOutcome(_phase, _reject, _phase == HandshakePhase.Accepted);
        }

        public void Dispose()
        {
            // Releases the capability cancellation source. Idempotent: a second
            // call finds no pending source. Session state is left as-is; the
            // owner has already retired this attempt via Cancel or Release.
            CancelPendingCapability();
        }

        private async Task<PlatformCapabilityResult> QueryCapabilityAsync(PlatformCapabilityQuery query, CancellationToken token)
        {
            try
            {
                return await _capabilities.QueryAsync(in query, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Includes synchronous provider throws, cancellation and delayed
                // faults. Retired attempts cannot leave an unobserved exception.
                return new PlatformCapabilityResult(query.Attempt, query.Generation, false);
            }
        }

        private void CancelPendingCapability()
        {
            _pendingCapability = null;
            CancellationTokenSource? cancellation = _capabilityCancellation;
            _capabilityCancellation = null;
            if (cancellation == null)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            catch (AggregateException)
            {
                // A provider's cancellation callback must not resurrect this attempt.
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }
}
