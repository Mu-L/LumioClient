#if LUMIO_NATIVE_LOADER
using Lumio.Client.Bot;
using ArchitectureTimerAbi = Lumio.Engine.NativeLoader.INativeTimerAbi;
using ArchitectureTimerHandle = Lumio.Engine.NativeLoader.NativeTimerHandle;
using ArchitectureDrainRecord = Lumio.Engine.NativeLoader.NativeTimerDrainRecord;

namespace Lumio.Client.Bot.Host;

/// <summary>Adapts the Architecture-owned NativeLoader timer wrapper to the Client timer contract.</summary>
internal sealed class NativeLoaderTimerAbi : INativeTimerAbi, IDisposable
{
    private readonly ArchitectureTimerAbi _inner;
    private bool _disposed;

    internal NativeLoaderTimerAbi(ArchitectureTimerAbi inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public static NativeLoaderTimerAbi Load(string nativePath)
        => new(Lumio.Engine.NativeLoader.NativeLoaderTimerAbi.Load(nativePath));

    public int CreateManager(uint mode, out IntPtr manager)
    {
        int status = _inner.CreateManager(mode, out nint nativeManager);
        manager = nativeManager;
        return status;
    }

    public int DestroyManager(IntPtr manager) => _inner.DestroyManager(manager);

    public int RegisterDispatch(IntPtr manager, uint dispatchId) => _inner.RegisterDispatch(manager, dispatchId);

    public int RegisterScope(IntPtr manager, ulong scopeId, uint scopeKind, out uint generation) =>
        _inner.RegisterScope(manager, scopeId, scopeKind, out generation);

    public int CreateSlot(IntPtr manager, out IntPtr slot)
    {
        int status = _inner.CreateSlot(manager, out nint nativeSlot);
        slot = nativeSlot;
        return status;
    }

    public int BindSlot(IntPtr manager, IntPtr slot, uint dispatchId) => _inner.BindSlot(manager, slot, dispatchId);

    public int ScheduleRepeating(
        IntPtr manager,
        ulong scopeId,
        uint scopeKind,
        uint scopeGeneration,
        ulong firstDue,
        ulong interval,
        IntPtr slot,
        out NativeTimerHandle handle)
    {
        int status = _inner.ScheduleRepeating(
            manager,
            scopeId,
            scopeKind,
            scopeGeneration,
            firstDue,
            interval,
            slot,
            out ArchitectureTimerHandle nativeHandle);
        handle = new NativeTimerHandle(nativeHandle.Index, nativeHandle.Generation, nativeHandle.Context);
        return status;
    }

    public int Advance(IntPtr manager, ulong toTick) => _inner.Advance(manager, toTick);

    public int Drain(IntPtr manager, Span<NativeTimerDrainRecord> records, out int count)
    {
        ArchitectureDrainRecord[] nativeRecords = new ArchitectureDrainRecord[Math.Max(records.Length, 1)];
        Span<ArchitectureDrainRecord> nativeSpan = records.Length == 0
            ? Span<ArchitectureDrainRecord>.Empty
            : nativeRecords;
        int status = _inner.Drain(manager, nativeSpan, out count);
        int copy = Math.Min(count, records.Length);
        for (int i = 0; i < copy; i++)
        {
            records[i] = new NativeTimerDrainRecord(
                nativeRecords[i].Due,
                nativeRecords[i].ScheduleSequence,
                nativeRecords[i].SlotDispatchId);
        }

        return status;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
#endif
