using System;
using System.Collections.Generic;
using System.Threading;
using Lumio.Client.Connection;
using Lumio.Client.Handshake;
using Lumio.Client.Input;
using Lumio.Client.Prediction;
using Lumio.Client.Replica;
using Lumio.GameRuntime.Ecs;

namespace Lumio.Client.Session
{
    internal sealed class ClientSession : IClientSession, IDisposable
    {
        private readonly ClientSessionDependencies _dependencies;
        private readonly SessionStateMachine _machine = new SessionStateMachine();
        private readonly SessionGenerationAllocator _generations = new SessionGenerationAllocator();
        private readonly SessionEventInbox _inbox = new SessionEventInbox();
        private readonly SessionResourceLedger _ledger = new SessionResourceLedger();
        private readonly RuntimeHandleLedger _handles = new RuntimeHandleLedger();
        private readonly GameplayScopeActivationGate _scopeGate = new GameplayScopeActivationGate();
        private readonly ClientConfigStagingArea _config = new ClientConfigStagingArea();
        private readonly ActiveMessageGate _messageGate = new ActiveMessageGate();
        private readonly PendingOutboundInputQueue _pendingOutbound = new PendingOutboundInputQueue();
        private readonly HandshakeOrchestrator _handshakeOrch = new HandshakeOrchestrator();
        private readonly FirstConnectOrchestrator _firstConnect = new FirstConnectOrchestrator();
        private readonly ScopeAndRuntimeActivationOrchestrator _activation = new ScopeAndRuntimeActivationOrchestrator();
        private readonly AuthorityUpdateOrchestrator _authority = new AuthorityUpdateOrchestrator();
        private readonly ResyncOrchestrator _resync = new ResyncOrchestrator();
        private readonly ReconnectOrchestrator _reconnect = new ReconnectOrchestrator();
        private readonly CloseOrchestrator _close = new CloseOrchestrator();
        private readonly AuthorityStageBundle _bundle = new AuthorityStageBundle();
        private readonly TerminalSessionState _terminal = new TerminalSessionState();
        private readonly object _gate = new object();
        private readonly ConnectionEvent[] _drainBuffer = new ConnectionEvent[ClientConnectionCreateRequest.DefaultDrainLimit];
        private IClientConnection _connection = default!;
        private IClientReplica _replica = default!;
        private IClientPrediction _prediction = default!;
        private bool _runtimeCommitted;
        private bool _baselineAck;
        private bool _pendingBaselineAck;
        private bool _presented;
        private int _replicaStages;
        private int _runtimeCalls;
        private ulong _snapshotSequence;
        private ulong _epoch;
        private ulong _authorityEpoch;
        private ClientEndpoint _endpoint;
        private bool _superseded;
        private bool _resourcesReleased = true;
        private bool _ticking;
        private bool _closeRequested;
        private bool _closeAsFault;
        private SessionSupersededNotice _pendingSuperseded;
        private bool _hasPendingSuperseded;

        public ClientSession(ClientSessionDependencies dependencies) { _dependencies = dependencies; }

        public SessionCommandResult RequestConnect(in SessionConnectRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_ticking) return new SessionCommandResult(false);
                if (_machine.State != ClientSessionState.Disconnected && _machine.State != ClientSessionState.Closed
                    && _machine.State != ClientSessionState.Reconnecting) return new SessionCommandResult(false);
                if (_machine.State == ClientSessionState.Closed) _terminal.Unfreeze();
                _superseded = false;
                _hasPendingSuperseded = false;
                _endpoint = request.Endpoint;
                return StartGeneration(request.Generation == 0 ? 1UL : request.Generation);
            }
        }

        public SessionCommandResult Login(in SessionConnectRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_ticking) return new SessionCommandResult(false);
                if (_machine.State != ClientSessionState.Superseded && _machine.State != ClientSessionState.Closed
                    && _machine.State != ClientSessionState.Disconnected) return new SessionCommandResult(false);
                ReleaseAll();
                _superseded = false;
                _hasPendingSuperseded = false;
                _terminal.Unfreeze();
                _endpoint = request.Endpoint;
                return StartGeneration(request.Generation == 0 ? 1UL : request.Generation);
            }
        }

        public SessionTickResult Tick(in ClientOwnerTick tick)
        {
            _ = tick;
            lock (_gate)
            {
                if (_ticking) return new SessionTickResult(_machine.State);
                _ticking = true;
                try
                {
                    if (_connection != null && !_machine.IsTerminal)
                    {
                        int n = _connection.DrainEvents(_drainBuffer);
                        for (int i = 0; i < n; i++)
                        {
                            ConnectionEvent evt = _drainBuffer[i];
                            _drainBuffer[i] = default;
                            if (evt.Generation.Value != _machine.Generation) continue;
                            SessionEventPriority priority = evt.Kind == ConnectionEventKind.FrameReceived
                                ? SessionEventArbiter.MapMessage(_dependencies.Messages.Map(evt.Frame.Bytes)) : SessionEventArbiter.MapConnection(evt.Kind);
                            if (!_inbox.Enqueue(priority, evt.Generation.Value, evt))
                            {
                                FailSession();
                                break;
                            }
                        }
                        // Lifecycle events preempt a pending authority operation. Other
                        // frames remain bounded and ordered until that operation completes.
                        while (!_machine.IsTerminal && !_closeRequested && _inbox.TryPeek(out SessionEvent next))
                        {
                            if (next.Generation != _machine.Generation)
                            {
                                _inbox.TryDequeue(out _);
                                continue;
                            }
                            bool lifecycle = next.Connection.Terminal || next.Priority == SessionEventPriority.Superseded
                                || next.Priority == SessionEventPriority.Fault;
                            if (!lifecycle && _authority.IsPending) break;
                            if (!lifecycle && _machine.State == ClientSessionState.Negotiating
                                && _handshakeOrch.Handshake.GetSnapshot().Phase == HandshakePhase.AwaitingCapability
                                && next.Connection.Kind == ConnectionEventKind.FrameReceived
                                && _dependencies.Messages.Map(next.Connection.Frame.Bytes) != SessionMessageKind.Unknown) break;
                            _inbox.TryDequeue(out next);
                            Dispatch(in next);
                        }
                        if (!_machine.IsTerminal && !_closeRequested && _authority.IsPending)
                        {
                            bool resync = _authority.TryComplete(out _, out bool presented, out bool committed, out bool indeterminate);
                            if (!_authority.IsPending) CompleteAuthority(_authorityEpoch, resync, presented, committed, indeterminate);
                        }
                        if (!_machine.IsTerminal && !_closeRequested && _machine.State == ClientSessionState.Negotiating)
                        {
                            // Completion need not coincide with a new transport frame.
                            HandshakeOutcome outcome = _handshakeOrch.Handshake.Poll();
                            if (outcome.Accepted && !TryEnterSynchronizing(outcome)) FailSession();
                            else if (outcome.Phase == HandshakePhase.Rejected) RequestClose(new SessionCloseRequest(false));
                        }
                        if (_machine.State == ClientSessionState.Active && !_superseded && !_closeRequested && !_authority.IsPending)
                        {
                            DrainReplicaOutbound();
                        }
                    }
                }
                catch (Exception) { FailSession(); }
                finally
                {
                    _ticking = false;
                    if (_closeRequested)
                    {
                        bool fault = _closeAsFault;
                        _closeRequested = _closeAsFault = false;
                        RequestClose(new SessionCloseRequest(fault));
                    }
                    Array.Clear(_drainBuffer, 0, _drainBuffer.Length);
                }
                return new SessionTickResult(_machine.State);
            }
        }

        public SessionCommandResult RequestClose(in SessionCloseRequest request)
        {
            lock (_gate)
            {
                // A gameplay/presentation callback may reenter Close. Defer
                // destruction until the current owner transaction leaves the stack.
                if (_ticking)
                {
                    _closeRequested = true;
                    _closeAsFault |= request.Fault;
                    return new SessionCommandResult(true);
                }
                if (_terminal.Frozen && _machine.IsTerminal) { ReleaseAll(); return new SessionCommandResult(true); }
                if (request.Fault) _machine.TryEnter(ClientSessionState.Faulted);
                ReleaseAll();
                if (!request.Fault && _machine.State != ClientSessionState.Faulted) _machine.TryEnter(ClientSessionState.Closed);
                _terminal.Freeze();
                return new SessionCommandResult(true);
            }
        }

        public void Dispose()
        {
            // Disposal is the existing non-fault close: it releases every owned
            // resource (including the authority orchestrator's cancellation
            // source) through ReleaseAll and is idempotent on a frozen terminal.
            RequestClose(new SessionCloseRequest(false));
        }

        public ClientSessionSnapshot GetSnapshot()
        {
            lock (_gate)
            {
                return new ClientSessionSnapshot(_machine.State, _machine.Generation, _runtimeCommitted,
                    _ledger.Count, _scopeGate.Activated, _baselineAck, _presented, _replicaStages, 0,
                    _runtimeCalls, _handshakeOrch.BeginCount, _handles.EcsCount, _handles.VoxelCount, _ledger.ReleaseOrder);
            }
        }

        public bool TryDequeueSuperseded(out SessionSupersededNotice notice)
        {
            lock (_gate)
            {
                if (!_hasPendingSuperseded) { notice = default; return false; }
                notice = _pendingSuperseded;
                _hasPendingSuperseded = false;
                return true;
            }
        }

        public bool TryGetReplicaWorld(out IReplicaWorld world)
        {
            lock (_gate)
            {
                if (_replica == null) { world = null!; return false; }
                world = _replica.World;
                return true;
            }
        }

        private SessionCommandResult StartGeneration(ulong generation)
        {
            _epoch++;
            _inbox.Clear();
            _authority.CancelPending();
            _pendingOutbound.Clear();
            _scopeGate.Reset();
            _config.Clear();
            _bundle.Clear();
            _messageGate.Reset();
            _generations.Seed(generation);
            _machine.SetGeneration(generation);
            _runtimeCommitted = _baselineAck = _pendingBaselineAck = _presented = false;
            _snapshotSequence = 0;
            _resourcesReleased = false;
            if (!_machine.TryEnter(ClientSessionState.Connecting)) return new SessionCommandResult(false);
            try
            {
                var request = new ClientConnectionCreateRequest(generation, ClientConnectionCreateRequest.DefaultEventCapacity,
                    ClientConnectionCreateRequest.DefaultDrainLimit, _endpoint);
                ClientConnectionCreateResult created = _dependencies.Connections.Create(in request, out _connection);
                if (!created.Succeeded) { FailSession(); return new SessionCommandResult(false); }
                _ledger.Acquire("connection");
                if (!_connection.Start().Succeeded) { FailSession(); return new SessionCommandResult(false); }
                IClientHandshake handshake = _dependencies.Handshakes.Create(_dependencies.Capabilities, _dependencies.HandshakeFrames);
                _handshakeOrch.Begin(handshake, new HandshakeAttemptId(generation), generation);
                _ledger.Acquire("handshake");
                _replica = _dependencies.Replicas.Create();
                _replica.ResetForNewSession(new ReplicaResetRequest(generation));
                _prediction = _dependencies.Predictions.Create(new PredictionCreateRequest(generation, 8));
                _ledger.Acquire("replica");
                _ledger.Acquire("prediction");
                _ledger.Acquire("input");
                _machine.TryEnter(ClientSessionState.Negotiating);
                return new SessionCommandResult(true);
            }
            catch (Exception) { FailSession(); return new SessionCommandResult(false); }
        }

        private void Dispatch(in SessionEvent evt)
        {
            // Recheck at consumption, not only when the batch was drained. A prior
            // event can close or replace this generation within the same Tick.
            if (_machine.IsTerminal || evt.Generation != _machine.Generation) return;
            if (evt.Priority == SessionEventPriority.Fault) { FailSession(); return; }
            if (evt.Priority == SessionEventPriority.ForcedClose || evt.Priority == SessionEventPriority.Cancel)
            {
                RequestClose(new SessionCloseRequest(false));
                return;
            }
            if (evt.Priority == SessionEventPriority.Disconnect) { HandleDisconnect(); return; }
            if (evt.Connection.Kind != ConnectionEventKind.FrameReceived) return;
            SessionMessageKind kind = _dependencies.Messages.Map(evt.Connection.Frame.Bytes);
            if (_machine.State == ClientSessionState.Negotiating && kind != SessionMessageKind.ConnectionSuperseded)
            {
                if (kind == SessionMessageKind.Welcome)
                {
                    // Existing welcome-only WorldChange slice. Release/manifest
                    // admission is still a separate integration requirement.
                    if (!TryValidateWelcome(evt.Connection.Frame.Bytes, evt.Generation)
                        || !TryEnterSynchronizing(new HandshakeOutcome(HandshakePhase.Accepted, HandshakeRejectReason.None, true)))
                    { FailSession(); return; }
                }
                else
                {
                    HandshakeOutcome outcome = _handshakeOrch.HandleOpaqueFrame(evt.Connection.Frame.Bytes);
                    if (outcome.Accepted && !TryEnterSynchronizing(outcome)) FailSession();
                    else if (outcome.Phase == HandshakePhase.Rejected) RequestClose(new SessionCloseRequest(false));
                    return;
                }
            }
            if (kind == SessionMessageKind.Gap && _machine.State == ClientSessionState.Active)
            {
                _resync.Enter(_dependencies.Commands, _machine.Generation);
                _machine.TryEnter(ClientSessionState.Resyncing);
                return;
            }
            if (!_messageGate.Allow(_machine.State, evt.Generation, _machine.Generation, kind)) return;
            if (kind == SessionMessageKind.ConnectionSuperseded) { HandleConnectionSuperseded(evt.Connection.Frame.Bytes); return; }
            if (kind == SessionMessageKind.Welcome)
            {
                if (_replica == null || !_replica.TryObserveWelcome(evt.Connection.Frame.Bytes)) FailSession();
                return;
            }
            if (kind == SessionMessageKind.Error) { FailSession(); return; }
            if (kind == SessionMessageKind.WorldChange || kind == SessionMessageKind.AuthorityUpdate)
                ApplyAuthority(evt.Connection.Frame.Bytes, _machine.State == ClientSessionState.Active ? ReplicaUpdateKind.Delta : ReplicaUpdateKind.FullSnapshot);
        }

        private bool TryEnterSynchronizing(HandshakeOutcome outcome)
        {
            if (!_firstConnect.TryEnterSynchronizing(outcome, _config, _activation, _dependencies.Scope,
                _scopeGate, _handles, _machine.Generation)) return false;
            _ledger.Acquire("scope");
            _ledger.Acquire("ecs");
            _ledger.Acquire("voxel");
            return _machine.TryEnter(ClientSessionState.Synchronizing);
        }

        private bool TryValidateWelcome(ReadOnlyMemory<byte> frame, ulong eventGeneration)
        {
            try
            {
                return WireCodec.DecodePack(frame.Span) is WelcomeMessage welcome && welcome.InstanceId != 0UL
                    && !welcome.Self.IsDefault && welcome.Self.InstanceId == welcome.InstanceId
                    && welcome.ConnectionGeneration != 0UL && welcome.ConnectionGeneration == eventGeneration
                    && welcome.ConnectionGeneration == _machine.Generation;
            }
            catch (Exception error) when (error is FormatException or ArgumentException) { return false; }
        }

        private void ApplyAuthority(ReadOnlyMemory<byte> update, ReplicaUpdateKind kind)
        {
            _replicaStages++;
            _runtimeCalls++;
            _authorityEpoch = _epoch;
            bool resync = _authority.TryCommit(_replica, _dependencies.Runtime, _dependencies.Presentation, _bundle,
                _machine.Generation, update, kind, _snapshotSequence + 1,
                out _, out bool presented, out bool committed, out bool indeterminate);
            if (!_authority.IsPending) CompleteAuthority(_authorityEpoch, resync, presented, committed, indeterminate);
        }

        private void CompleteAuthority(ulong epoch, bool resync, bool presented, bool committed, bool indeterminate)
        {
            if (epoch != _epoch || _machine.IsTerminal || _closeRequested) return;
            if (indeterminate) { FailSession(); return; }
            if (committed)
            {
                _snapshotSequence++;
                _runtimeCommitted = true;
                _pendingBaselineAck = _endpoint.InitialFrame.IsEmpty;
                _presented = presented;
                _dependencies.Commands.SetBufferPolicy(new InputBufferPolicy(InputBufferPolicyKind.Hold, _machine.Generation));
                _machine.TryEnter(ClientSessionState.Active);
                return;
            }
            if (resync)
            {
                _resync.Enter(_dependencies.Commands, _machine.Generation);
                _machine.TryEnter(ClientSessionState.Resyncing);
            }
            else if (_machine.State == ClientSessionState.Synchronizing
                || _authority.LastStageStatus == ReplicaStageStatus.Rejected
                || _authority.LastStageStatus == ReplicaStageStatus.Frozen) FailSession();
        }

        private void HandleConnectionSuperseded(ReadOnlyMemory<byte> utf8)
        {
            if (_replica == null || !_replica.TryObserveConnectionSuperseded(utf8, out ReplicaConnectionSuperseded observed) || !observed.Received) return;
            _superseded = true;
            _dependencies.Commands.SetBufferPolicy(new InputBufferPolicy(InputBufferPolicyKind.Drop, _machine.Generation));
            _pendingSuperseded = new SessionSupersededNotice(observed.Received, observed.ReasonCode, observed.NetEntityId, observed.NewConnectionGeneration);
            _hasPendingSuperseded = true;
            ReleaseAll();
            _machine.TryEnter(ClientSessionState.Superseded);
            _terminal.Freeze();
        }

        private void HandleDisconnect()
        {
            if (_superseded)
            {
                ReleaseAll();
                _machine.TryEnter(ClientSessionState.Superseded);
                _terminal.Freeze();
                return;
            }
            ulong next = _reconnect.NextGeneration(_generations);
            ReleaseAll();
            _machine.TryEnter(ClientSessionState.Reconnecting);
            StartGeneration(next);
        }

        private void DrainReplicaOutbound()
        {
            if (_replica == null || _connection == null) return;
            if (_pendingBaselineAck)
            {
                if (!_connection.TrySend(new EncodedFrame(SessionWireBytes.BaselineAck)).Accepted) return;
                _pendingBaselineAck = false;
                _baselineAck = true;
            }
            FlushPendingOutbound();
            if (_machine.IsTerminal) return;
            IReadOnlyList<WorldMessage> outbound = _replica.World.DrainOutbound();
            for (int i = 0; i < outbound.Count; i++)
            {
                if (outbound[i] is not InputCommandMessage input) continue;
                byte[] encoded = WireCodec.EncodeInput(input);
                if (!_pendingOutbound.TryEnqueue(input, encoded)) { FailSession(); return; }
            }
            FlushPendingOutbound();
        }

        private void FlushPendingOutbound()
        {
            if (_connection == null) return;
            while (_pendingOutbound.TryPeek(out PendingOutboundInput pending))
            {
                if (!_connection.TrySend(new EncodedFrame(pending.EncodedBytes)).Accepted) return;
                _pendingOutbound.TryDequeue(out pending);
                _dependencies.OutboundObserver.Observe(pending.Message, pending.EncodedBytes);
                if (_machine.IsTerminal || _closeRequested) return;
            }
        }

        private void FailSession()
        {
            _machine.TryEnter(ClientSessionState.Faulted);
            _terminal.Freeze();
            ReleaseAll();
        }

        private void ReleaseAll()
        {
            _inbox.Clear();
            if (_resourcesReleased) return;
            _resourcesReleased = true;
            _epoch++;
            try
            {
                _authority.CancelPending();
                _close.Release(_ledger, _handles, _scopeGate, _dependencies.Scope, _dependencies.Input,
                    _handshakeOrch.Handshake, _connection, _replica, _prediction, _machine.Generation);
            }
            catch (Exception) { _machine.TryEnter(ClientSessionState.Faulted); _terminal.Freeze(); }
            finally
            {
                // Dispose is not part of the transport interface; honor it when
                // provided by a socket adapter, even if another cleanup step failed.
                if (_connection is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch (Exception) { _machine.TryEnter(ClientSessionState.Faulted); }
                }
                // Same for the handshake: it owns a capability cancellation source.
                if (_handshakeOrch.Handshake is IDisposable handshakeDisposable)
                {
                    try { handshakeDisposable.Dispose(); }
                    catch (Exception) { _machine.TryEnter(ClientSessionState.Faulted); }
                }
                // Same for the authority orchestrator: it owns the runtime
                // cancellation source of a pending commit.
                try { _authority.Dispose(); }
                catch (Exception) { _machine.TryEnter(ClientSessionState.Faulted); }
                _handshakeOrch.Clear();
                _connection = null!;
                _replica = null!;
                _prediction = null!;
                _pendingOutbound.Clear();
                _pendingBaselineAck = false;
                _config.Clear();
                _bundle.Clear();
            }
        }
    }
}
