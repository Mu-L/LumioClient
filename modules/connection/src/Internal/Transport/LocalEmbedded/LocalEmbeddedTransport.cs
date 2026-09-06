using System;

namespace Lumio.Client.Connection
{
    internal sealed class LocalEmbeddedTransport
    {
        private readonly LocalEmbeddedEndpointPair _pair;
        private int _encodeCalls;
        private int _decodeCalls;

        public LocalEmbeddedTransport(int capacity)
        {
            _pair = new LocalEmbeddedEndpointPair(capacity);
        }

        public LocalEmbeddedEndpointPair Pair
        {
            get { return _pair; }
        }

        public int EncodeCalls
        {
            get { return _encodeCalls; }
        }

        public int DecodeCalls
        {
            get { return _decodeCalls; }
        }

        public bool TrySendClient(in EncodedFrame frame)
        {
            if (!TryEncode(in frame, out ReadOnlyMemory<byte> bytes))
            {
                return false;
            }

            return _pair.Client.TrySend(bytes);
        }

        public bool TryReceiveServer(out EncodedFrame frame)
        {
            if (!_pair.Server.TryReceive(out ReadOnlyMemory<byte> bytes))
            {
                frame = default(EncodedFrame);
                return false;
            }

            return TryDecode(bytes, out frame);
        }

        public bool TrySendServer(in EncodedFrame frame)
        {
            if (!TryEncode(in frame, out ReadOnlyMemory<byte> bytes))
            {
                return false;
            }

            return _pair.Server.TrySend(bytes);
        }

        public bool TryReceiveClient(out EncodedFrame frame)
        {
            if (!_pair.Client.TryReceive(out ReadOnlyMemory<byte> bytes))
            {
                frame = default(EncodedFrame);
                return false;
            }

            return TryDecode(bytes, out frame);
        }

        // LocalEmbedded 走不透明字节:传输层只复制、不解释 payload,形状由 wire 契约在上层决定。
        internal bool TryEncode(in EncodedFrame frame, out ReadOnlyMemory<byte> bytes)
        {
            bytes = default(ReadOnlyMemory<byte>);
            if (frame.Bytes.IsEmpty)
            {
                return false;
            }

            byte[] copy = new byte[frame.Bytes.Length];
            frame.Bytes.Span.CopyTo(copy);
            bytes = copy;
            _encodeCalls++;
            return true;
        }

        internal bool TryDecode(ReadOnlyMemory<byte> bytes, out EncodedFrame frame)
        {
            frame = default(EncodedFrame);
            if (bytes.IsEmpty)
            {
                return false;
            }

            byte[] copy = new byte[bytes.Length];
            bytes.Span.CopyTo(copy);
            frame = new EncodedFrame(copy);
            _decodeCalls++;
            return true;
        }
    }
}
