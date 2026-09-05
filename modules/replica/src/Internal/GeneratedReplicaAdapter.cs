using System;
using Lumio.GameRuntime.Ecs;

namespace Lumio.Client.Replica
{
    internal sealed class GeneratedReplicaAdapter
    {
        private readonly bool _enabled = true;

        public bool TryValidate(ReplicaUpdateKind kind, ReadOnlyMemory<byte> update)
        {
            if (!_enabled || update.IsEmpty)
            {
                return false;
            }

            if (kind != ReplicaUpdateKind.FullSnapshot && kind != ReplicaUpdateKind.Delta)
            {
                return false;
            }

            try
            {
                return WireCodec.DecodePack(update.Span) is WorldChangeMessage;
            }
            catch (Exception error) when (error is FormatException or ArgumentException)
            {
                return false;
            }
        }
    }
}
