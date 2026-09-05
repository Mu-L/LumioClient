using System.Text;
using Lumio.Client.Replica;
using Lumio.GameRuntime.Ecs;

namespace Lumio.Client.Replica.Tests.Contract;

public sealed class ReplicaGeneratedFixtureTests
{
    [Fact]
    public void RuntimeWorldChangeIsTheOnlyAcceptedPlan()
    {
        var adapter = new GeneratedReplicaAdapter();
        byte[] valid = WireCodec.EncodePack(new WorldChangeMessage(
            1,
            0,
            Array.Empty<CreateRecord>(),
            Array.Empty<FieldChange>(),
            Array.Empty<DestroyRecord>(),
            Array.Empty<ClientRpcRecord>()));
        Assert.True(adapter.TryValidate(ReplicaUpdateKind.FullSnapshot, valid));
        Assert.True(adapter.TryValidate(ReplicaUpdateKind.Delta, valid));
        Assert.False(adapter.TryValidate(ReplicaUpdateKind.FullSnapshot, ReadOnlyMemory<byte>.Empty));
        Assert.False(adapter.TryValidate(ReplicaUpdateKind.Delta, ReadOnlyMemory<byte>.Empty));
        Assert.False(adapter.TryValidate(ReplicaUpdateKind.FullSnapshot, Encoding.UTF8.GetBytes("{\"messageType\":\"FullSnapshot\"}")));
        Assert.False(adapter.TryValidate(ReplicaUpdateKind.Delta, Encoding.UTF8.GetBytes("{\"messageType\":\"Delta\"}")));
    }
}
