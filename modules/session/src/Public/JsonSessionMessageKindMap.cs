using System;
using Lumio.GameRuntime.Ecs;

namespace Lumio.Client.Session
{
    public sealed class JsonSessionMessageKindMap : ISessionMessageKindMap
    {
        private static readonly byte[] GapMagic = { 0x91, 0xA9, 0xB0, 0xC3 };

        public SessionMessageKind Map(ReadOnlyMemory<byte> frame)
        {
            ReadOnlySpan<byte> span = frame.Span;
            if (span.SequenceEqual(GapMagic))
            {
                return SessionMessageKind.Gap;
            }

            try
            {
                return WireCodec.DecodePack(span) switch
                {
                    WelcomeMessage => SessionMessageKind.Welcome,
                    WorldChangeMessage => SessionMessageKind.WorldChange,
                    ConnectionSupersededMessage => SessionMessageKind.ConnectionSuperseded,
                    ErrorMessage => SessionMessageKind.Error,
                    _ => SessionMessageKind.Unknown,
                };
            }
            catch (Exception error) when (error is FormatException or ArgumentException)
            {
                return SessionMessageKind.Unknown;
            }
        }
    }
}
