#if LUMIO_NATIVE_LOADER
using Lumio.Client.Bot;
using Lumio.Client.Bot.Host;
using EngineTimerHandle = Lumio.Engine.NativeLoader.NativeTimerHandle;
using EngineTimerDrainRecord = Lumio.Engine.NativeLoader.NativeTimerDrainRecord;
using EngineTimerAbi = Lumio.Engine.NativeLoader.INativeTimerAbi;

namespace Lumio.Client.Bot.Tests.Unit;

public sealed class NativeLoaderTimerAbiTests
{
    [Fact]
    public void ProductionAdapterUsesArchitecturePublicWrapperWithoutPrivateAbiAccess()
    {
        string sourcePath = Path.Combine(RepoRoot(), "modules", "bot", "host", "NativeLoaderTimerAbi.cs");
        string source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("System.Reflection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Marshal", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_library", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RootApiWithTimers", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetExport", source, StringComparison.Ordinal);
        Assert.Contains("Lumio.Engine.NativeLoader.NativeLoaderTimerAbi", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterForwardsCadenceThroughArchitecturePublicTimerApi()
    {
        var engineAbi = new RecordingEngineTimerAbi();
        using var adapter = new NativeLoaderTimerAbi(engineAbi);
        using var timer = new ClientTimerManager(adapter);

        Assert.True(timer.ScheduleBotChatCadence());
        Assert.Equal(new ulong[] { 5, 10, 15 }, timer.Advance(15).ToArray());
        Assert.Equal(new ulong[] { 5, 10, 15 }, timer.Trace.UtteranceTicks.ToArray());
        Assert.Equal(1, engineAbi.CreateManagerCalls);
        Assert.Equal(1, engineAbi.ScheduleRepeatingCalls);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LumioClient.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }

    private sealed class RecordingEngineTimerAbi : EngineTimerAbi
    {
        private ulong _tick;

        public int CreateManagerCalls { get; private set; }

        public int ScheduleRepeatingCalls { get; private set; }

        public int CreateManager(uint mode, out nint manager)
        {
            _ = mode;
            CreateManagerCalls++;
            manager = 1;
            return 0;
        }

        public int DestroyManager(nint manager)
        {
            _ = manager;
            return 0;
        }

        public int RegisterDispatch(nint manager, uint dispatchId)
        {
            _ = manager;
            _ = dispatchId;
            return 0;
        }

        public int RegisterScope(nint manager, ulong scopeId, uint scopeKind, out uint generation)
        {
            _ = manager;
            _ = scopeId;
            _ = scopeKind;
            generation = 1;
            return 0;
        }

        public int TeardownScope(nint manager, ulong scopeId)
        {
            _ = manager;
            _ = scopeId;
            return 0;
        }

        public int CreateSlot(nint manager, out nint slot)
        {
            _ = manager;
            slot = 2;
            return 0;
        }

        public int BindSlot(nint manager, nint slot, uint dispatchId)
        {
            _ = manager;
            _ = slot;
            _ = dispatchId;
            return 0;
        }

        public int CloseSlot(nint manager, nint slot)
        {
            _ = manager;
            _ = slot;
            return 0;
        }

        public int ScheduleOneShot(
            nint manager,
            ulong scopeId,
            uint scopeKind,
            uint scopeGeneration,
            ulong due,
            nint slot,
            out EngineTimerHandle handle)
        {
            _ = manager;
            _ = scopeId;
            _ = scopeKind;
            _ = scopeGeneration;
            _ = due;
            _ = slot;
            handle = new EngineTimerHandle(1, 1, 1);
            return 0;
        }

        public int ScheduleRepeating(
            nint manager,
            ulong scopeId,
            uint scopeKind,
            uint scopeGeneration,
            ulong firstDue,
            ulong interval,
            nint slot,
            out EngineTimerHandle handle)
        {
            _ = manager;
            _ = scopeId;
            _ = scopeKind;
            _ = scopeGeneration;
            _ = firstDue;
            _ = interval;
            _ = slot;
            ScheduleRepeatingCalls++;
            handle = new EngineTimerHandle(1, 1, 1);
            return 0;
        }

        public int Advance(nint manager, ulong toTick)
        {
            _ = manager;
            _tick = toTick;
            return 0;
        }

        public int Pump(nint manager, ulong nowMs)
        {
            _ = manager;
            _ = nowMs;
            return 0;
        }

        public int Cancel(nint manager, in EngineTimerHandle handle)
        {
            _ = manager;
            _ = handle;
            return 0;
        }

        public int Drain(nint manager, Span<EngineTimerDrainRecord> records, out int count)
        {
            _ = manager;
            count = (int)Math.Min(_tick / ClientTimerManager.BotChatCadenceTicks, (ulong)records.Length);
            for (int i = 0; i < count; i++)
            {
                ulong due = (ulong)(i + 1) * ClientTimerManager.BotChatCadenceTicks;
                records[i] = new EngineTimerDrainRecord(due, (ulong)(i + 1), ClientTimerManager.BotChatCadenceDispatch);
            }

            return 0;
        }
    }
}
#endif
