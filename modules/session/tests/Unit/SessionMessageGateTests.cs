namespace Lumio.Client.Session.Tests.Unit;

public sealed class SessionMessageGateTests
{
    [Fact]
    public void InvalidMatrix_HasZeroLeafCalls()
    {
        var gate = new ActiveMessageGate();
        Assert.False(gate.Allow(ClientSessionState.Negotiating, 1, 1, SessionMessageKind.Welcome));
        Assert.False(gate.Allow(ClientSessionState.Active, 2, 1, SessionMessageKind.WorldChange));
        Assert.False(gate.Allow(ClientSessionState.Active, 1, 1, SessionMessageKind.Unknown));
        Assert.Equal(3, gate.RejectedCalls);
    }

    [Fact]
    public void RuntimeCodecClassifiesTheFiveC1Messages()
    {
        var map = new JsonSessionMessageKindMap();
        var self = new Lumio.GameRuntime.Ecs.NetEntityId(7, 1);

        Assert.Equal(
            SessionMessageKind.Welcome,
            map.Map(Lumio.GameRuntime.Ecs.WireCodec.EncodePack(
                new Lumio.GameRuntime.Ecs.WelcomeMessage(7, self, 1, "self"))));
        Assert.Equal(
            SessionMessageKind.WorldChange,
            map.Map(Lumio.GameRuntime.Ecs.WireCodec.EncodePack(
                new Lumio.GameRuntime.Ecs.WorldChangeMessage(
                    1,
                    0,
                    Array.Empty<Lumio.GameRuntime.Ecs.CreateRecord>(),
                    Array.Empty<Lumio.GameRuntime.Ecs.FieldChange>(),
                    Array.Empty<Lumio.GameRuntime.Ecs.DestroyRecord>(),
                    Array.Empty<Lumio.GameRuntime.Ecs.ClientRpcRecord>()))));
        Assert.Equal(
            SessionMessageKind.ConnectionSuperseded,
            map.Map(Lumio.GameRuntime.Ecs.WireCodec.EncodePack(
                new Lumio.GameRuntime.Ecs.ConnectionSupersededMessage(self, 2))));
        Assert.Equal(
            SessionMessageKind.Error,
            map.Map(Lumio.GameRuntime.Ecs.WireCodec.EncodePack(
                new Lumio.GameRuntime.Ecs.ErrorMessage("runtime_failure", "failure"))));
    }
}

public sealed class GameplayScopeActivationGateTests
{
    [Fact]
    public void ScopeMustActivateBeforeWorldHandles()
    {
        var gate = new GameplayScopeActivationGate();
        Assert.False(gate.CanCreateWorldHandles());
        Assert.True(gate.TryPrepare());
        Assert.True(gate.TryActivate());
        Assert.True(gate.CanCreateWorldHandles());
    }
}
