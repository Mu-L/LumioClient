using System;

namespace Lumio.Client.Connection
{
    internal sealed class ConnectionEventQueue
    {
        private readonly ConnectionEvent[] _items;
        private int _head;
        private int _count;
        // A terminal notification must survive even when the data queue is full.
        private ConnectionEvent _terminal;
        private bool _hasTerminal;

        public ConnectionEventQueue(int capacity)
        {
            _items = new ConnectionEvent[Math.Max(capacity, 1)];
        }

        public bool TryEnqueue(in ConnectionEvent evt)
        {
            if (evt.Terminal)
            {
                if (_hasTerminal) return false;
                _terminal = evt;
                _hasTerminal = true;
                return true;
            }
            if (_hasTerminal || _count == _items.Length) return false;
            _items[(_head + _count) % _items.Length] = evt;
            _count++;
            return true;
        }

        public int Count => _count + (_hasTerminal ? 1 : 0);

        public void Clear()
        {
            Array.Clear(_items, 0, _items.Length);
            _head = 0;
            _count = 0;
            _terminal = default;
            _hasTerminal = false;
        }

        public int Drain(Span<ConnectionEvent> destination)
        {
            int n = Math.Min(destination.Length, _count);
            for (int i = 0; i < n; i++)
            {
                int slot = (_head + i) % _items.Length;
                destination[i] = _items[slot];
                _items[slot] = default; // Do not retain drained frame buffers.
            }
            _head = (_head + n) % _items.Length;
            _count -= n;
            // Preserve FIFO on an ordinary close (e.g. Superseded followed by Close).
            if (_hasTerminal && _count == 0 && n < destination.Length)
            {
                destination[n++] = _terminal;
                _terminal = default;
                _hasTerminal = false;
            }
            return n;
        }
    }
}
