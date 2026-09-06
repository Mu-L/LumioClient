using Lumio.Client.Input;

namespace Lumio.Client.Input.Tests.Unit;

public sealed class InputBudgetRegressionTests
{
    [Fact]
    public void PartialDrainRetainsAcceptedSamplesInOrder()
    {
        var ingress = new InputSampleIngress(4);
        var mapper = new RecordingMapper();
        var source = new InputCommandSource(ingress, mapper);
        Enqueue(ingress, 3);
        var one = new GameplayCommandCandidate[1];
        Assert.Equal(1, source.DrainCandidates(one, new InputDrainContext(1, 1)));
        Assert.Equal(1, source.DrainCandidates(one, new InputDrainContext(1, 1)));
        Assert.Equal(1, source.DrainCandidates(one, new InputDrainContext(1, 1)));
        Assert.Equal(0, source.DrainCandidates(one, new InputDrainContext(1, 1)));
        Assert.Equal(new ulong[] { 1, 2, 3 }, mapper.Sequences);
    }

    [Fact]
    public void ZeroOutputBudgetDoesNotDrainIngress()
    {
        var ingress = new InputSampleIngress(4);
        var source = new InputCommandSource(ingress, new RecordingMapper());
        Enqueue(ingress, 2);
        Assert.Equal(0, source.DrainCandidates(Span<GameplayCommandCandidate>.Empty, new InputDrainContext(1, 4)));
        var output = new GameplayCommandCandidate[4];
        Assert.Equal(0, source.DrainCandidates(output, new InputDrainContext(1, 0)));
        Assert.Equal(2, source.DrainCandidates(output, new InputDrainContext(1, 4)));
    }

    [Fact]
    public void ResyncHoldsSamplesUntilExplicitlyReleased()
    {
        var ingress = new InputSampleIngress(4);
        var source = new InputCommandSource(ingress, new RecordingMapper());
        Enqueue(ingress, 2);
        var output = new GameplayCommandCandidate[4];
        source.SetBufferPolicy(new InputBufferPolicy(InputBufferPolicyKind.Resync, 1));
        Assert.Equal(0, source.DrainCandidates(output, new InputDrainContext(1, 4)));
        source.SetBufferPolicy(new InputBufferPolicy(InputBufferPolicyKind.Hold, 1));
        Assert.Equal(2, source.DrainCandidates(output, new InputDrainContext(1, 4)));
    }

    [Fact]
    public void DropClearsRetainedBatchEvenWithZeroBudget()
    {
        var ingress = new InputSampleIngress(4);
        var source = new InputCommandSource(ingress, new RecordingMapper());
        Enqueue(ingress, 3);
        var one = new GameplayCommandCandidate[1];
        Assert.Equal(1, source.DrainCandidates(one, new InputDrainContext(1, 1)));
        source.SetBufferPolicy(new InputBufferPolicy(InputBufferPolicyKind.Drop, 1));
        Assert.Equal(0, source.DrainCandidates(Span<GameplayCommandCandidate>.Empty, new InputDrainContext(1, 0)));
        source.SetBufferPolicy(new InputBufferPolicy(InputBufferPolicyKind.Hold, 1));
        Assert.Equal(0, source.DrainCandidates(one, new InputDrainContext(1, 1)));
    }

    [Fact]
    public void RetainedBatchCannotCrossGeneration()
    {
        var ingress = new InputSampleIngress(4);
        var source = new InputCommandSource(ingress, new RecordingMapper());
        Enqueue(ingress, 3);
        var one = new GameplayCommandCandidate[1];
        Assert.Equal(1, source.DrainCandidates(one, new InputDrainContext(1, 1)));
        Assert.Equal(0, source.DrainCandidates(one, new InputDrainContext(2, 1)));
    }

    private static void Enqueue(InputSampleIngress ingress, int count)
    {
        for (int i = 0; i < count; i++)
        {
            ingress.TryEnqueue(new RawInputSample((uint)(i + 1), 0, 0));
        }
    }

    private sealed class RecordingMapper : IGameInputMapper
    {
        public List<ulong> Sequences { get; } = new();

        public bool TryMap(in SequencedInputSample sample, in InputDrainContext context, out GameplayCommandCandidate candidate)
        {
            Sequences.Add(sample.Sequence.Value);
            candidate = new GameplayCommandCandidate(sample.Sequence, ReadOnlyMemory<byte>.Empty);
            return true;
        }
    }
}
