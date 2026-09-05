using Lumio.Client.Connection;

namespace Lumio.Client.Session
{
    internal enum SessionEventPriority
    {
        Fault = 0,
        Superseded = 1,
        ForcedClose = 2,
        Cancel = 3,
        StableReject = 4,
        Disconnect = 5,
        CriticalQueueFull = 6,
        Gap = 7,
        Retryable = 8,
        Success = 9,
        Normal = 10
    }

    internal readonly struct SessionEvent
    {
        public SessionEvent(SessionEventPriority priority, ulong generation, ulong sequence, ConnectionEvent connection)
        {
            Priority = priority;
            Generation = generation;
            Sequence = sequence;
            Connection = connection;
        }

        public SessionEventPriority Priority { get; }

        public ulong Generation { get; }

        public ulong Sequence { get; }

        public ConnectionEvent Connection { get; }
    }
}
