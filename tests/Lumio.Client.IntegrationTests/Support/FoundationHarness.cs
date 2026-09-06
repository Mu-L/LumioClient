using Lumio.Client.Connection;
using Lumio.Client.Handshake;
using Lumio.Client.Input;
using Lumio.Client.Observability;
using Lumio.Client.Persistence;
using Lumio.Client.Prediction;
using Lumio.Client.Replica;
using Lumio.Client.Session;
using Lumio.GameRuntime.Ecs;

namespace Lumio.Client.IntegrationTests.Support;

internal static class FoundationTestBytes
{
    public static readonly byte[] Hello = { 0xA5, 0x3C, 0x91, 0x07, 0xD2, 0x4E, 0xB8, 0x11 };
    public static readonly byte[] Reject = { 0x5A, 0xC3, 0x0E, 0xF4 };
    private static readonly NetEntityId Self = new(7UL, 2UL);
    public static readonly byte[] Welcome = WireCodec.EncodePack(new WelcomeMessage(7UL, Self, 1UL));
    public static readonly byte[] WorldChange = WireCodec.EncodePack(new WorldChangeMessage(1UL, 0UL,
        new[]
        {
            new CreateRecord("WorldEntity", new NetEntityId(7UL, 1UL), Array.Empty<FieldValue>()),
            new CreateRecord("PlayerEntity", Self, Array.Empty<FieldValue>()),
        }, Array.Empty<FieldChange>(), Array.Empty<DestroyRecord>(), Array.Empty<ClientRpcRecord>()));
    public static readonly byte[] Snapshot = WorldChange;
    public static readonly byte[] Gap = { 0x91, 0xA9, 0xB0, 0xC3 };
}

internal sealed class FoundationHarness
{
    public FoundationHarness(bool runtimeCommitted) : this(runtimeCommitted, false) { }
    public FoundationHarness(bool runtimeCommitted, IClientOutboundMessageObserver outboundObserver)
        : this(runtimeCommitted, false, outboundObserver) { }
    public FoundationHarness(bool runtimeCommitted, bool indeterminate)
        : this(runtimeCommitted, indeterminate, new NullClientOutboundMessageObserver()) { }
    public FoundationHarness(bool runtimeCommitted, IPlatformCapabilityProvider capabilities)
        : this(runtimeCommitted, false, new NullClientOutboundMessageObserver(), capabilities) { }

    public FoundationHarness(bool runtimeCommitted, IClientPresentationSink presentation)
        : this(runtimeCommitted, false, new NullClientOutboundMessageObserver(), null, presentation) { }

    private FoundationHarness(bool runtimeCommitted, bool indeterminate,
        IClientOutboundMessageObserver outboundObserver, IPlatformCapabilityProvider? capabilities = null, IClientPresentationSink? presentationOverride = null)
    {
        Connections = new CapturingConnectionFactory();
        Scope = new ImmediateGameplayScopeActivator();
        Presentation = new NullPresentationSink();
        Runtime = new RecordingRuntime(runtimeCommitted, indeterminate);
        Ingress = new InputSampleIngress(16);
        Commands = new InputCommandSource(Ingress, new PassThroughMapper());
        var options = new ClientEventPipelineOptions(8, 4, TimeSpan.FromSeconds(1));
        new ClientEventPipelineFactory().Create(in options, new InMemoryClientEventSink(8), out var writer);
        var deps = new ClientSessionDependencies(Connections, new ClientHandshakeFactory(), capabilities ?? new OkCapability(),
            new HelloClassifier(), Ingress, Commands, IClientPersistenceFactory.CreateMemory().CreateVerifiedSessionArtifactSource(),
            writer, Runtime, new ClientReplicaFactory(), new ClientPredictionFactory(), Scope, presentationOverride ?? Presentation,
            new JsonSessionMessageKindMap(), outboundObserver);
        new ClientSessionFactory().Create(in deps, out var session);
        Session = session;
    }

    public CapturingConnectionFactory Connections { get; }
    public ImmediateGameplayScopeActivator Scope { get; }
    public NullPresentationSink Presentation { get; }
    public RecordingRuntime Runtime { get; }
    public InputSampleIngress Ingress { get; }
    public IInputCommandSource Commands { get; }
    public IClientSession Session { get; }
    public void Connect(ulong generation = 1) => Session.RequestConnect(new SessionConnectRequest(generation), CancellationToken.None);
    public void Tick() => Session.Tick(new ClientOwnerTick(1));
    public void Deliver(byte[] bytes) => Connections.Loopback.TryDeliverToClient(new EncodedFrame(bytes));
    public void HappyPathToActive()
    {
        Connect(); Tick();
        Deliver(FoundationTestBytes.Hello); Tick();
        Deliver(FoundationTestBytes.Welcome); Tick();
        Deliver(FoundationTestBytes.WorldChange); Tick();
    }

    internal sealed class CapturingConnectionFactory : IClientConnectionFactory
    {
        private readonly Queue<bool> _sendResults = new();
        public LocalEmbeddedLoopback Loopback { get; private set; } = default!;
        public int CreateCount { get; private set; }
        public int StartCount { get; private set; }
        public int CloseCount { get; private set; }
        public bool DisconnectAfterFrameDrain { get; set; }
        public List<byte[]> SendAttempts { get; } = new();
        public void QueueSendResults(params bool[] results)
        { foreach (bool result in results) _sendResults.Enqueue(result); }
        public ClientConnectionCreateResult Create(in ClientConnectionCreateRequest request, out IClientConnection connection)
        {
            ClientConnectionCreateResult result = new ClientConnectionFactory().Create(in request, out IClientConnection inner);
            Loopback = result.Loopback;
            CreateCount++;
            connection = new CountingConnection(inner, this);
            return result;
        }
        private sealed class CountingConnection : IClientConnection
        {
            private readonly IClientConnection _inner;
            private readonly CapturingConnectionFactory _owner;
            public CountingConnection(IClientConnection inner, CapturingConnectionFactory owner) { _inner = inner; _owner = owner; }
            public ConnectionGeneration Generation => _inner.Generation;
            public ConnectionCommandResult Start() { _owner.StartCount++; return _inner.Start(); }
            public ConnectionSendResult TrySend(in EncodedFrame frame)
            {
                _owner.SendAttempts.Add(frame.Bytes.ToArray());
                if (_owner._sendResults.Count > 0 && !_owner._sendResults.Dequeue()) return new ConnectionSendResult(false);
                return _inner.TrySend(in frame);
            }
            public int DrainEvents(Span<ConnectionEvent> destination)
            {
                int count = _inner.DrainEvents(destination);
                bool hasFrame = false;
                for (int i = 0; i < count; i++)
                    if (destination[i].Kind == ConnectionEventKind.FrameReceived) { hasFrame = true; break; }
                if (!_owner.DisconnectAfterFrameDrain || !hasFrame) return count;
                _owner.DisconnectAfterFrameDrain = false;
                _inner.RequestClose(ConnectionCloseReason.Disconnect);
                if (count < destination.Length) count += _inner.DrainEvents(destination.Slice(count));
                return count;
            }
            public ConnectionCommandResult RequestClose(ConnectionCloseReason reason)
            { _owner.CloseCount++; return _inner.RequestClose(reason); }
            public ClientConnectionSnapshot GetSnapshot() => _inner.GetSnapshot();
        }
    }

    private sealed class HelloClassifier : IHandshakeFrameClassifier
    {
        public HandshakeOpaqueFrameRole Classify(ReadOnlyMemory<byte> frame)
        {
            if (frame.Span.SequenceEqual(FoundationTestBytes.Hello)) return HandshakeOpaqueFrameRole.ServerHello;
            if (frame.Span.SequenceEqual(FoundationTestBytes.Reject)) return HandshakeOpaqueFrameRole.HandshakeReject;
            return HandshakeOpaqueFrameRole.Unclassified;
        }
    }
    private sealed class OkCapability : IPlatformCapabilityProvider
    {
        public ValueTask<PlatformCapabilityResult> QueryAsync(in PlatformCapabilityQuery query, CancellationToken cancellationToken)
            => new(new PlatformCapabilityResult(query.Attempt, query.Generation, true));
    }
    private sealed class PassThroughMapper : IGameInputMapper
    {
        public bool TryMap(in SequencedInputSample sample, in InputDrainContext context, out GameplayCommandCandidate candidate)
        { candidate = new GameplayCommandCandidate(sample.Sequence, new byte[] { 0x42 }); return true; }
    }
}

internal sealed class RecordingRuntime : IClientRuntimePort
{
    private readonly bool _committed;
    private readonly bool _indeterminate;
    public RecordingRuntime(bool committed) : this(committed, false) { }
    public RecordingRuntime(bool committed, bool indeterminate) { _committed = committed; _indeterminate = indeterminate; }
    public int AuthorityCalls { get; private set; }
    public int LocalCalls { get; private set; }
    public ValueTask<RuntimeTransactionOutcome> ApplyAuthoritativeTransaction(in RuntimeTransactionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AuthorityCalls++;
        if (_indeterminate) return new(RuntimeTransactionOutcome.IndeterminateOutcome());
        // Recording does not replace the real stage application on successful paths.
        return new(_committed ? request.CommitAuthority() : new RuntimeTransactionOutcome(false));
    }
    public ValueTask<RuntimeTransactionOutcome> ApplyLocalPrediction(in RuntimeTransactionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LocalCalls++;
        return new(_indeterminate ? RuntimeTransactionOutcome.IndeterminateOutcome() : new RuntimeTransactionOutcome(_committed));
    }
}
