using System.Collections.Generic;
using Lumio.Client.Connection;

namespace Lumio.Client.Session
{
    internal sealed class SessionEventInbox
    {
        private const int Capacity = 256;
        private readonly List<SessionEvent> _items = new List<SessionEvent>(Capacity);
        private ulong _sequence;

        public bool Enqueue(SessionEventPriority priority, ulong generation, ConnectionEvent connection)
        {
            if (_items.Count == Capacity) return false;
            _items.Add(new SessionEvent(priority, generation, ++_sequence, connection));
            return true;
        }

        public bool TryPeek(out SessionEvent evt)
        {
            if (_items.Count == 0) { evt = default; return false; }
            evt = _items[BestIndex()];
            return true;
        }

        public bool TryDequeue(out SessionEvent evt)
        {
            if (_items.Count == 0) { evt = default; return false; }
            int index = BestIndex();
            evt = _items[index];
            _items.RemoveAt(index);
            return true;
        }

        public void Clear() { _items.Clear(); }

        private int BestIndex()
        {
            int best = 0;
            for (int i = 1; i < _items.Count; i++)
            {
                SessionEvent candidate = _items[i];
                SessionEvent current = _items[best];
                if (candidate.Priority < current.Priority
                    || (candidate.Priority == current.Priority && candidate.Sequence < current.Sequence)) best = i;
            }
            return best;
        }
    }
}
