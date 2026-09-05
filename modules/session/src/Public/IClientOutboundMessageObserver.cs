using System;
using Lumio.GameRuntime.Ecs;

namespace Lumio.Client.Session
{
    /// <summary>Observes typed client messages after Runtime wire encoding and acceptance by the connection.</summary>
    public interface IClientOutboundMessageObserver
    {
        void Observe(InputCommandMessage message, ReadOnlyMemory<byte> encodedBytes);
    }

    /// <summary>No-op outbound observer for clients that do not collect wire evidence.</summary>
    public sealed class NullClientOutboundMessageObserver : IClientOutboundMessageObserver
    {
        public void Observe(InputCommandMessage message, ReadOnlyMemory<byte> encodedBytes)
        {
            _ = message;
            _ = encodedBytes;
        }
    }
}
