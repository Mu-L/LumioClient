using System.Reflection;
using System.Text;
using Lumio.Client.Replica;
using Lumio.GameRuntime.Ecs;

namespace Lumio.Client.Replica.Tests.Contract;

public sealed class ReplicaC1StrictTests
{
    [Theory]
    [InlineData(ReplicaUpdateKind.FullSnapshot, "{\"messageType\":\"FullSnapshot\",\"tickId\":0,\"revision\":0,\"stateBlocks\":[]}")]
    [InlineData(ReplicaUpdateKind.Delta, "{\"messageType\":\"Delta\",\"tickId\":1,\"revision\":1,\"changedBlocks\":[]}")]
    public void LegacySnapshotAndDeltaFramesAreRejected(ReplicaUpdateKind kind, string legacy)
    {
        IClientReplica replica = new ClientReplicaFactory().Create();
        replica.ResetForNewSession(new ReplicaResetRequest(1));
        Assert.True(replica.TryObserveWelcome(WireCodec.EncodePack(new WelcomeMessage(1, new NetEntityId(1, 1), 1))));

        var request = new ReplicaStageRequest(
            1,
            kind,
            1,
            0,
            1,
            1,
            Encoding.UTF8.GetBytes(legacy),
            ReadOnlyMemory<ulong>.Empty,
            ReadOnlyMemory<ulong>.Empty);
        Assert.Equal(ReplicaStageStatus.Rejected, replica.StageAuthority(in request, out _, out _).Status);
    }

    [Fact]
    public void LegacyFrameIsRejectedEvenWhenWatermarkLooksDuplicate()
    {
        IClientReplica replica = new ClientReplicaFactory().Create();
        replica.ResetForNewSession(new ReplicaResetRequest(1));
        NetEntityId self = new(1, 1);
        Assert.True(replica.TryObserveWelcome(WireCodec.EncodePack(new WelcomeMessage(1, self, 1))));
        byte[] runtime = WireCodec.EncodePack(new WorldChangeMessage(
            1,
            0,
            new[]
            {
                new CreateRecord("world", new NetEntityId(1, 2), Array.Empty<FieldValue>()),
                new CreateRecord("player", self, Array.Empty<FieldValue>()),
            },
            Array.Empty<FieldChange>(),
            Array.Empty<DestroyRecord>(),
            Array.Empty<ClientRpcRecord>()));
        ReplicaStageRequest valid = new(1, ReplicaUpdateKind.FullSnapshot, 10, 0, 1, 1, runtime, ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<ulong>.Empty);
        Assert.Equal(ReplicaStageStatus.Staged, replica.StageAuthority(in valid, out ReplicaStageHandle handle, out _).Status);
        Assert.Equal(ReplicaOutcomeStatus.Observed, replica.ObserveRuntimeOutcome(handle, ReplicaRuntimeOutcome.CommittedOutcome(), out _));

        ReplicaStageRequest legacy = new(1, ReplicaUpdateKind.FullSnapshot, 10, 0, 1, 1, Encoding.UTF8.GetBytes("{\"messageType\":\"FullSnapshot\",\"tickId\":0,\"revision\":0,\"stateBlocks\":[]}"), ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<ulong>.Empty);
        Assert.Equal(ReplicaStageStatus.Rejected, replica.StageAuthority(in legacy, out _, out _).Status);
    }

    [Fact]
    public void PublicReplicaWorldHasNoInstallAdmissionFallback()
    {
        Assert.Null(typeof(IReplicaWorld).GetMethod("InstallAdmission", BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(typeof(IReplicaWorld).Assembly.GetExportedTypes(), type =>
            type.Name is "ReplicaAdmission" or "ReplicaAdmissionResult");
    }

    [Fact]
    public void SupersededFrameIsAppliedByRuntimeWorldManager()
    {
        IClientReplica replica = new ClientReplicaFactory().Create();
        replica.ResetForNewSession(new ReplicaResetRequest(1));
        NetEntityId self = new(1, 1);
        Assert.True(replica.TryObserveWelcome(WireCodec.EncodePack(new WelcomeMessage(1, self, 1))));
        replica.World.Manager.Enqueue(new WorldChangeMessage(
            1,
            0,
            new[] { new CreateRecord("player", self, Array.Empty<FieldValue>()) },
            Array.Empty<FieldChange>(),
            Array.Empty<DestroyRecord>(),
            Array.Empty<ClientRpcRecord>()));
        replica.World.Manager.Tick();

        byte[] frame = WireCodec.EncodePack(new ConnectionSupersededMessage(self, 2));
        Assert.True(replica.TryObserveConnectionSuperseded(frame, out ReplicaConnectionSuperseded notice));
        Assert.Equal(self.ToHex(), notice.NetEntityId);
        Assert.Equal(2UL, notice.NewConnectionGeneration);
        Assert.False(replica.World.InputEnabled);
        Assert.True(replica.World.Manager.World.IsLive(self));
        Assert.False(replica.World.Manager.World.Get<ObserverComponent>(self).Connected);
        Assert.Equal(2UL, replica.World.Manager.World.Get<ObserverComponent>(self).ConnectionGeneration);
    }

    [Fact]
    public void DestroyReasonKeepsLeftAoiNonTerminalAndTerminatedTerminal()
    {
        IClientReplica replica = new ClientReplicaFactory().Create();
        replica.ResetForNewSession(new ReplicaResetRequest(1));
        NetEntityId self = new(1, 1);
        NetEntityId left = new(1, 2);
        NetEntityId terminated = new(1, 3);
        Assert.True(replica.TryObserveWelcome(WireCodec.EncodePack(new WelcomeMessage(1, self, 1))));

        Commit(replica, 1, 1, new WorldChangeMessage(
            1,
            0,
            new[]
            {
                new CreateRecord("world", new NetEntityId(1, 4), Array.Empty<FieldValue>()),
                new CreateRecord("player", self, Array.Empty<FieldValue>()),
                new CreateRecord("bot", left, Array.Empty<FieldValue>()),
                new CreateRecord("bot", terminated, Array.Empty<FieldValue>())
            },
            Array.Empty<FieldChange>(),
            Array.Empty<DestroyRecord>(),
            Array.Empty<ClientRpcRecord>()));

        Commit(replica, 2, 2, new WorldChangeMessage(
            2,
            0,
            Array.Empty<CreateRecord>(),
            Array.Empty<FieldChange>(),
            new[]
            {
                new DestroyRecord(left, DestroyReason.LeftAoi),
                new DestroyRecord(terminated, DestroyReason.Terminated)
            },
            Array.Empty<ClientRpcRecord>()));

        Assert.False(replica.World.Manager.World.IsTombstoned(left));
        Assert.True(replica.World.Manager.World.IsTombstoned(terminated));
    }

    private static void Commit(IClientReplica replica, ulong sequence, ulong revision, WorldChangeMessage change)
    {
        ReplicaStageRequest request = new(
            1,
            sequence == 1 ? ReplicaUpdateKind.FullSnapshot : ReplicaUpdateKind.Delta,
            10,
            sequence == 1 ? 0 : revision - 1,
            revision,
            sequence,
            WireCodec.EncodePack(change),
            ReadOnlyMemory<ulong>.Empty,
            ReadOnlyMemory<ulong>.Empty);
        ReplicaStageResult staged = replica.StageAuthority(in request, out ReplicaStageHandle handle, out _);
        Assert.Equal(ReplicaStageStatus.Staged, staged.Status);
        Assert.Equal(ReplicaOutcomeStatus.Observed, replica.ObserveRuntimeOutcome(handle, ReplicaRuntimeOutcome.CommittedOutcome(), out _));
    }
}
