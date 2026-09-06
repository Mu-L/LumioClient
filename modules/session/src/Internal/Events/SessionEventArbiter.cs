using Lumio.Client.Connection;

namespace Lumio.Client.Session
{
    internal sealed class SessionEventArbiter
    {
        public static SessionEventPriority MapConnection(ConnectionEventKind kind)
        {
            switch (kind)
            {
                case ConnectionEventKind.Faulted: return SessionEventPriority.Fault;
                case ConnectionEventKind.Closed: return SessionEventPriority.ForcedClose;
                case ConnectionEventKind.Disconnected: return SessionEventPriority.Disconnect;
                case ConnectionEventKind.Started: return SessionEventPriority.Success;
                default: return SessionEventPriority.Normal;
            }
        }

        public static SessionEventPriority MapMessage(SessionMessageKind kind)
        {
            if (kind == SessionMessageKind.Error) return SessionEventPriority.Fault;
            if (kind == SessionMessageKind.Gap) return SessionEventPriority.Gap;
            if (kind == SessionMessageKind.ConnectionSuperseded) return SessionEventPriority.Superseded;
            return SessionEventPriority.Normal;
        }
    }
}
