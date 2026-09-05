using Lumio.GameRuntime.Ecs;

namespace Lumio.Client.Replica
{
    internal static class ReplicaNetIds
    {
        public static bool TryParse(string text, out NetEntityId id)
        {
            return NetEntityId.TryParse(text, out id) && !id.IsDefault;
        }

        public static string Format(NetEntityId id)
        {
            return id.ToHex();
        }
    }
}
