using System;

namespace Lumio.Client.Session
{
    public enum SessionMessageKind
    {
        Unknown = 0,
        Welcome = 1,
        WorldChange = 2,
        Gap = 3,
        AuthorityUpdate = 4,
        ConnectionSuperseded = 5,
        Error = 6
    }

    public interface ISessionMessageKindMap
    {
        SessionMessageKind Map(ReadOnlyMemory<byte> frame);
    }

    public sealed class UnpublishedSessionMessageKindMap : ISessionMessageKindMap
    {
        public SessionMessageKind Map(ReadOnlyMemory<byte> frame)
        {
            _ = frame;
            return SessionMessageKind.Unknown;
        }
    }
}
