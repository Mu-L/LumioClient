using System;

namespace Lumio.Client.Session
{
    internal sealed class ActiveMessageGate
    {
        public int RejectedCalls { get; private set; }

        public void Reset()
        {
            RejectedCalls = 0;
        }

        public bool Allow(ClientSessionState state, ulong eventGeneration, ulong sessionGeneration, SessionMessageKind kind)
        {
            if (eventGeneration != sessionGeneration)
            {
                RejectedCalls++;
                return false;
            }

            if (kind == SessionMessageKind.Unknown)
            {
                RejectedCalls++;
                return false;
            }

            if (kind == SessionMessageKind.ConnectionSuperseded
                && (state == ClientSessionState.Negotiating
                    || state == ClientSessionState.Synchronizing
                    || state == ClientSessionState.Resyncing
                    || state == ClientSessionState.Active))
            {
                return true;
            }

            if (state == ClientSessionState.Synchronizing || state == ClientSessionState.Resyncing)
            {
                return kind == SessionMessageKind.Welcome
                    || kind == SessionMessageKind.WorldChange
                    || kind == SessionMessageKind.Error;
            }

            if (state == ClientSessionState.Active)
            {
                return kind == SessionMessageKind.WorldChange
                    || kind == SessionMessageKind.Gap
                    || kind == SessionMessageKind.AuthorityUpdate
                    || kind == SessionMessageKind.ConnectionSuperseded
                    || kind == SessionMessageKind.Error;
            }

            RejectedCalls++;
            return false;
        }
    }
}
