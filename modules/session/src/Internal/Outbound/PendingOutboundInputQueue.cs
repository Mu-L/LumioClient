using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Ecs;

namespace Lumio.Client.Session
{
    internal readonly struct PendingOutboundInput
    {
        public PendingOutboundInput(InputCommandMessage message, byte[] encodedBytes)
        {
            Message = message;
            EncodedBytes = encodedBytes;
        }

        public InputCommandMessage Message { get; }

        public byte[] EncodedBytes { get; }
    }

    internal sealed class PendingOutboundInputQueue
    {
        public const int DefaultCapacity = 64;

        private readonly int _capacity;
        private readonly Queue<PendingOutboundInput> _items = new Queue<PendingOutboundInput>();

        public PendingOutboundInputQueue()
            : this(DefaultCapacity)
        {
        }

        internal PendingOutboundInputQueue(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _capacity = capacity;
        }

        public int Count
        {
            get { return _items.Count; }
        }

        public int Capacity
        {
            get { return _capacity; }
        }

        public bool TryEnqueue(InputCommandMessage message, ReadOnlyMemory<byte> encodedBytes)
        {
            if (_items.Count >= _capacity)
            {
                return false;
            }

            _items.Enqueue(new PendingOutboundInput(message, encodedBytes.ToArray()));
            return true;
        }

        public bool TryPeek(out PendingOutboundInput item)
        {
            if (_items.Count == 0)
            {
                item = default(PendingOutboundInput);
                return false;
            }

            item = _items.Peek();
            return true;
        }

        public bool TryDequeue(out PendingOutboundInput item)
        {
            if (_items.Count == 0)
            {
                item = default(PendingOutboundInput);
                return false;
            }

            item = _items.Dequeue();
            return true;
        }

        public void Clear()
        {
            _items.Clear();
        }
    }
}
