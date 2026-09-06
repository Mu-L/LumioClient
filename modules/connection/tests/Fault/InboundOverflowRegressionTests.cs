using Lumio.Client.Connection;

namespace Lumio.Client.Connection.Tests.Fault;

public sealed class InboundOverflowRegressionTests
{
    [Fact]
    public void SaturatedInboundQueueDisconnectsAndPreservesValidatedFrames()
    {
        var machine = new ConnectionStateMachine(new ConnectionGeneration(1), 1);
        machine.Start();
        var events = new ConnectionEvent[4];
        machine.Drain(events);
        Assert.True(machine.TryDeliverInbound(new EncodedFrame(new byte[] { 1 })));
        Assert.False(machine.TryDeliverInbound(new EncodedFrame(new byte[] { 2 })));
        Assert.True(machine.Terminal);
        Assert.False(machine.CanSend(new EncodedFrame(new byte[] { 3 })));
        // 已入队的第 1 帧得到保留，随后交付 Disconnected 终止通知
        Assert.Equal(2, machine.Drain(events));
        Assert.Equal(ConnectionEventKind.FrameReceived, events[0].Kind);
        Assert.Equal(ConnectionEventKind.Disconnected, events[1].Kind);
        Assert.Equal(0, machine.Drain(events));
    }

    [Fact]
    public void TerminalEventHasReservedCapacityAndPreservesPrecedingControlFrames()
    {
        var machine = new ConnectionStateMachine(new ConnectionGeneration(7), 1);
        machine.Start();
        var events = new ConnectionEvent[1];
        machine.Drain(events);
        Assert.True(machine.TryDeliverInbound(new EncodedFrame(new byte[] { 9 })));
        Assert.True(machine.TryClose(ConnectionCloseReason.Disconnect));
        Assert.Equal(1, machine.Drain(events));
        Assert.Equal(ConnectionEventKind.FrameReceived, events[0].Kind);
        Assert.Equal(1, machine.Drain(events));
        Assert.Equal(ConnectionEventKind.Disconnected, events[0].Kind);
        Assert.Equal(7UL, events[0].Generation.Value);
        Assert.Equal(0, machine.Drain(events));
    }
}
