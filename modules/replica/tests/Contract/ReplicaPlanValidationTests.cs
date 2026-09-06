using System.Text;
using Lumio.Client.Replica;
using Lumio.GameRuntime.Ecs;

namespace Lumio.Client.Replica.Tests.Contract;

public sealed class ReplicaPlanValidationTests
{
    [Fact]
    public void RuntimeWorldChangeIsTheOnlyAcceptedPlan()
    {
        byte[] valid = WireCodec.EncodePack(new WorldChangeMessage(
            1,
            0,
            Array.Empty<CreateRecord>(),
            Array.Empty<FieldChange>(),
            Array.Empty<DestroyRecord>(),
            Array.Empty<ClientRpcRecord>()));
        Assert.True(RuntimeReplicaPlanAdapter.TryValidate(ReplicaUpdateKind.FullSnapshot, valid));
        Assert.True(RuntimeReplicaPlanAdapter.TryValidate(ReplicaUpdateKind.Delta, valid));
        Assert.False(RuntimeReplicaPlanAdapter.TryValidate(ReplicaUpdateKind.FullSnapshot, ReadOnlyMemory<byte>.Empty));
        Assert.False(RuntimeReplicaPlanAdapter.TryValidate(ReplicaUpdateKind.Delta, ReadOnlyMemory<byte>.Empty));
        Assert.False(RuntimeReplicaPlanAdapter.TryValidate(ReplicaUpdateKind.FullSnapshot, Encoding.UTF8.GetBytes("{\"messageType\":\"FullSnapshot\"}")));
        Assert.False(RuntimeReplicaPlanAdapter.TryValidate(ReplicaUpdateKind.Delta, Encoding.UTF8.GetBytes("{\"messageType\":\"Delta\"}")));
    }
}
