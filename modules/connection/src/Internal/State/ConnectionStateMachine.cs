using System;

namespace Lumio.Client.Connection
{
    internal sealed class ConnectionStateMachine
    {
        private readonly ConnectionGeneration _generation;
        private readonly ConnectionEventQueue _events;
        private bool _started;
        private bool _terminal;

        public ConnectionStateMachine(ConnectionGeneration generation, int eventCapacity)
        {
            _generation = generation;
            _events = new ConnectionEventQueue(eventCapacity);
        }

        public ConnectionGeneration Generation => _generation;
        public bool Terminal => _terminal;
        public int EventCount => _events.Count;

        public ConnectionCommandResult Start()
        {
            if (_terminal || _started) return new ConnectionCommandResult(false);
            _started = true;
            _events.TryEnqueue(new ConnectionEvent(ConnectionEventKind.Started, _generation, false));
            return new ConnectionCommandResult(true);
        }

        public bool CanSend(in EncodedFrame frame)
            => _started && !_terminal && !frame.Bytes.IsEmpty;

        public bool TryClose(ConnectionCloseReason reason)
        {
            if (_terminal) return false;
            ConnectionEventKind kind = reason == ConnectionCloseReason.Disconnect
                ? ConnectionEventKind.Disconnected
                : reason == ConnectionCloseReason.Fault
                    ? ConnectionEventKind.Faulted : ConnectionEventKind.Closed;
            _terminal = true;
            _events.TryEnqueue(new ConnectionEvent(kind, _generation, true));
            return true;
        }

        public bool TryDeliverLate(ConnectionGeneration generation)
            => generation.Value == _generation.Value && !_terminal;

        public bool TryDeliverInbound(in EncodedFrame frame)
        {
            if (_terminal || !_started) return false;
            if (_events.TryEnqueue(new ConnectionEvent(ConnectionEventKind.FrameReceived, _generation, false, frame)))
                return true;
            // Retain validated frames already in queue; emit Disconnect so the session can reconnect.
            TryClose(ConnectionCloseReason.Disconnect);
            return false;
        }

        public bool TryDeliverDisconnect() => TryClose(ConnectionCloseReason.Disconnect);
        public int Drain(Span<ConnectionEvent> destination) => _events.Drain(destination);
    }
}
