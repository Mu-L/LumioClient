using Lumio.Client.Replica;

namespace Lumio.Client.Session
{
    internal sealed class AuthorityStageBundle
    {
        public ReplicaStageHandle Replica { get; set; }

        public bool ReplicaStaged { get; set; }

        public void Clear()
        {
            ReplicaStaged = false;
        }
    }
}
