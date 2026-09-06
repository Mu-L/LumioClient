using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Lumio.Client.Connection
{
    /// <summary>Opaque-message ClientWebSocket transport. Protocol validation belongs to its consumer.</summary>
    internal sealed class WebSocketClientConnection : IClientConnection, IDisposable
    {
        private readonly object _gate = new object();
        private readonly ConnectionStateMachine _machine;
        private readonly ConnectionSendQueue _sendQueue;
        private readonly FaultDecoratingTransport _faults;
        private readonly ClientEndpoint _endpoint;
        private readonly WebSocketTransportOptions _options;
        private readonly int _drainLimit;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly SemaphoreSlim _sendSignal = new SemaphoreSlim(0);
        private readonly ManualResetEventSlim _openSignal = new ManualResetEventSlim(false);
        private ClientWebSocket? _socket;
        private Task? _pump;
        private Task? _sender;
        private bool _openSucceeded;
        private bool _disposed;
        private string? _negotiatedSubProtocol;
        private int _largestReceiveAllocationBytes;
        private long _applicationBytesReceived;
        private int _inboundDroppedByQueueFull;
        private bool _channelAuthRejected;
        private bool _idleDeadlineExpired;
        private bool _oversizeRejected;

        private WebSocketClientConnection(in ClientConnectionCreateRequest request,
            WebSocketTransportOptions options, ITransportFaultPolicy faultPolicy, string? rejection)
        {
            _endpoint = request.Endpoint;
            _options = options;
            _drainLimit = Math.Max(request.DrainLimit, 1);
            _faults = new FaultDecoratingTransport(faultPolicy ?? new PassThroughFaultPolicy());
            int capacity = Math.Max(request.EventCapacity, 1);
            _machine = new ConnectionStateMachine(request.Generation, capacity);
            _sendQueue = new ConnectionSendQueue(capacity);
            RejectionReason = rejection;
            if (rejection != null)
            {
                _machine.TryClose(ConnectionCloseReason.Fault);
                _openSignal.Set();
            }
        }

        internal static WebSocketClientConnection Create(in ClientConnectionCreateRequest request,
            WebSocketTransportOptions options, ITransportFaultPolicy faultPolicy)
            => new WebSocketClientConnection(in request, options, faultPolicy, null);
        internal static WebSocketClientConnection Rejected(in ClientConnectionCreateRequest request,
            WebSocketTransportOptions options, string reason)
            => new WebSocketClientConnection(in request, options, new PassThroughFaultPolicy(), reason);

        internal string? RejectionReason { get; }
        internal string? NegotiatedSubProtocol { get { lock (_gate) return _negotiatedSubProtocol; } }
        internal int LargestReceiveAllocationBytes { get { lock (_gate) return _largestReceiveAllocationBytes; } }
        internal long ApplicationBytesReceived { get { lock (_gate) return _applicationBytesReceived; } }
        internal int InboundDroppedByQueueFull { get { lock (_gate) return _inboundDroppedByQueueFull; } }
        internal bool ChannelAuthRejected { get { lock (_gate) return _channelAuthRejected; } }
        internal bool IdleDeadlineExpired { get { lock (_gate) return _idleDeadlineExpired; } }
        internal bool OversizeRejected { get { lock (_gate) return _oversizeRejected; } }

        internal string DescribeForDiagnostics()
        {
            lock (_gate)
                return string.Concat("websocket connection generation ",
                    _machine.Generation.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ", endpoint ", _endpoint.ToString(), ", subprotocol ", _negotiatedSubProtocol ?? "(unnegotiated)",
                    ", terminal ", _machine.Terminal ? "yes" : "no");
        }

        internal bool WaitForOpen(TimeSpan timeout)
        {
            try { if (!_openSignal.Wait(timeout)) return false; }
            catch (ObjectDisposedException) { return false; }
            lock (_gate) return !_disposed && _openSucceeded;
        }

        internal bool DeliverCallback(ConnectionGeneration generation)
        { lock (_gate) return !_disposed && _machine.TryDeliverLate(generation); }
        public ConnectionGeneration Generation { get { lock (_gate) return _machine.Generation; } }

        public ConnectionCommandResult Start()
        {
            lock (_gate)
            {
                if (_disposed) return new ConnectionCommandResult(false);
                ConnectionCommandResult result = _machine.Start();
                if (!result.Succeeded) return result;
                // Publish the task before Dispose can observe the started state.
                _pump = Task.Run(RunAsync);
                return result;
            }
        }

        public ConnectionSendResult TrySend(in EncodedFrame frame)
        {
            lock (_gate)
            {
                if (_disposed || !_machine.CanSend(in frame) || frame.Bytes.Length > _options.MaxMessageBytes
                    || !_sendQueue.TryEnqueue(in frame)) return new ConnectionSendResult(false);
                _sendSignal.Release();
                return new ConnectionSendResult(true);
            }
        }

        public int DrainEvents(Span<ConnectionEvent> destination)
        {
            lock (_gate)
            {
                int limit = Math.Min(destination.Length, _drainLimit);
                return limit <= 0 ? 0 : _machine.Drain(destination.Slice(0, limit));
            }
        }

        public ConnectionCommandResult RequestClose(ConnectionCloseReason reason)
        {
            bool closed;
            lock (_gate) closed = _machine.TryClose(reason);
            if (closed) { SignalOpen(); BeginShutdown(); }
            return new ConnectionCommandResult(closed);
        }

        public ClientConnectionSnapshot GetSnapshot()
        { lock (_gate) return new ClientConnectionSnapshot(_machine.Generation, _machine.Terminal, _machine.EventCount); }

        private async Task RunAsync()
        {
            ClientWebSocket socket;
            lock (_gate)
            {
                if (_disposed) return;
                socket = new ClientWebSocket();
                _socket = socket;
            }
            try
            {
                if (_endpoint.RequiresMvpChannelAuth)
                {
                    socket.Options.AddSubProtocol(MvpChannelAuth.SubProtocol);
                    socket.Options.AddSubProtocol(MvpChannelAuth.ToBase64Url(_endpoint.Credential.Span));
                    socket.Options.AddSubProtocol(MvpChannelAuth.ToBase64Url(_endpoint.Nonce.Span));
                }
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
                {
                    connect.CancelAfter(_endpoint.ConnectTimeout);
                    await socket.ConnectAsync(new Uri(_endpoint.Uri), connect.Token).ConfigureAwait(false);
                }
                if (_endpoint.RequiresMvpChannelAuth && !string.Equals(socket.SubProtocol, MvpChannelAuth.SubProtocol, StringComparison.Ordinal))
                {
                    Terminate(ConnectionCloseReason.Fault);
                    return;
                }
                if (!_endpoint.InitialFrame.IsEmpty)
                {
                    if (_endpoint.InitialFrame.Length > _options.MaxMessageBytes) { Terminate(ConnectionCloseReason.Fault); return; }
                    await SendOneAsync(socket, new EncodedFrame(_endpoint.InitialFrame), _shutdown.Token).ConfigureAwait(false);
                }
                lock (_gate)
                {
                    if (_disposed || _machine.Terminal) return;
                    _negotiatedSubProtocol = socket.SubProtocol;
                    _openSucceeded = true;
                    _sender = Task.Run(() => RunSendLoopAsync(socket, _shutdown.Token));
                }
                SignalOpen();
                await ReceiveLoopAsync(socket, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception) { Terminate(ConnectionCloseReason.Fault); }
            finally { SignalOpen(); }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            byte[] buffer = new byte[_options.ReceiveBufferBytes];
            var assembler = new WebSocketMessageAssembler(_options.MaxMessageBytes);
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    idle.CancelAfter(_options.IdleTimeout);
                    try { result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), idle.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        lock (_gate) _idleDeadlineExpired = true;
                        Terminate(ConnectionCloseReason.Disconnect);
                        return;
                    }
                }
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    bool denied = socket.CloseStatus == WebSocketCloseStatus.PolicyViolation;
                    lock (_gate) _channelAuthRejected = denied;
                    Terminate(denied ? ConnectionCloseReason.Fault : ConnectionCloseReason.Disconnect);
                    return;
                }
                if (!assembler.TryAppend(buffer, result.Count))
                {
                    lock (_gate)
                    {
                        _oversizeRejected = true;
                        _largestReceiveAllocationBytes = Math.Max(_largestReceiveAllocationBytes, assembler.LargestAllocationBytes);
                    }
                    Terminate(ConnectionCloseReason.Fault);
                    return;
                }
                if (!result.EndOfMessage) continue;
                byte[] complete = assembler.Complete();
                bool delivered;
                lock (_gate)
                {
                    _largestReceiveAllocationBytes = Math.Max(_largestReceiveAllocationBytes, assembler.LargestAllocationBytes);
                    _applicationBytesReceived += complete.Length;
                    delivered = _machine.TryDeliverInbound(new EncodedFrame(complete));
                    if (!delivered && !_disposed) _inboundDroppedByQueueFull++;
                }
                if (!delivered)
                {
                    // The state machine has latched Faulted on overflow. It is
                    // already terminal, so do not depend on a second TryClose succeeding.
                    SignalOpen();
                    BeginShutdown();
                    return;
                }
            }
            if (!token.IsCancellationRequested) Terminate(ConnectionCloseReason.Fault);
        }

        private async Task RunSendLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await _sendSignal.WaitAsync(token).ConfigureAwait(false);
                    while (TryTakeForSend(out EncodedFrame frame, out TransportFaultAction action))
                    {
                        if (action == TransportFaultAction.Drop) continue;
                        await SendOneAsync(socket, frame, token).ConfigureAwait(false);
                        if (action == TransportFaultAction.Duplicate) await SendOneAsync(socket, frame, token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception) { Terminate(ConnectionCloseReason.Fault); }
        }

        private static Task SendOneAsync(ClientWebSocket socket, EncodedFrame frame, CancellationToken token)
            => socket.SendAsync(new ArraySegment<byte>(frame.Bytes.ToArray()), WebSocketMessageType.Text, true, token);

        private bool TryTakeForSend(out EncodedFrame frame, out TransportFaultAction action)
        {
            lock (_gate)
            {
                action = TransportFaultAction.Pass;
                if (_machine.Terminal || !_sendQueue.TryPeek(out frame)) { frame = default; return false; }
                action = _faults.Next(0);
                _sendQueue.TryDequeue(out _);
                return true;
            }
        }

        private void Terminate(ConnectionCloseReason reason)
        {
            lock (_gate) _machine.TryClose(reason);
            SignalOpen();
            BeginShutdown();
        }

        private void SignalOpen()
        {
            try { _openSignal.Set(); }
            catch (ObjectDisposedException) { }
        }

        private void BeginShutdown()
        {
            ClientWebSocket? socket;
            lock (_gate) socket = _socket;
            try { _shutdown.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
            try { socket?.Abort(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _machine.TryClose(ConnectionCloseReason.OwnerRequest);
            }
            SignalOpen();
            BeginShutdown();
            // Never block the owner Tick waiting for socket worker threads. Their
            // exceptions are observed and synchronization objects outlive both pumps.
            _ = DisposeAfterPumpsAsync();
            GC.SuppressFinalize(this);
        }

        private async Task DisposeAfterPumpsAsync()
        {
            Task? pump;
            lock (_gate) pump = _pump;
            if (pump != null) { try { await pump.ConfigureAwait(false); } catch (Exception) { } }
            Task? sender;
            lock (_gate) sender = _sender;
            if (sender != null) { try { await sender.ConfigureAwait(false); } catch (Exception) { } }
            ClientWebSocket? socket;
            lock (_gate) { socket = _socket; _socket = null; }
            socket?.Dispose();
            _shutdown.Dispose();
            _sendSignal.Dispose();
            _openSignal.Dispose();
        }
    }
}
