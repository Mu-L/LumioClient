using Lumio.Client.Connection;

namespace Lumio.Client.Connection.Tests.Fault;

public sealed class ConnectionQueueFullTests
{
    // 队列本体这一层：数据槽位满时普通事件必须被拒绝且既有事件不被覆盖；终止事件享有独立
    // 保留槽位，满队列仍可交付终止事件，且第二条终止事件被拒。
    [Fact]
    public void FullEventQueue_RejectsWithoutSilentlyOverwriting()
    {
        var queue = new ConnectionEventQueue(2);
        var first = new ConnectionEvent(ConnectionEventKind.Started, new ConnectionGeneration(1), false);
        var second = new ConnectionEvent(ConnectionEventKind.FrameReceived, new ConnectionGeneration(1), false);
        var overflow = new ConnectionEvent(ConnectionEventKind.FrameReceived, new ConnectionGeneration(1), false);
        var terminal = new ConnectionEvent(ConnectionEventKind.Closed, new ConnectionGeneration(1), true);

        Assert.True(queue.TryEnqueue(in first));
        Assert.True(queue.TryEnqueue(in second));
        // 数据槽满：普通事件被拒绝
        Assert.False(queue.TryEnqueue(in overflow));
        // 终止槽位：接收终止事件
        Assert.True(queue.TryEnqueue(in terminal));
        // 保留槽位只有一个，不再重复接收
        Assert.False(queue.TryEnqueue(in terminal));
        Assert.Equal(3, queue.Count);

        var drained = new ConnectionEvent[4];
        Assert.Equal(3, queue.Drain(drained));
        Assert.Equal(ConnectionEventKind.Started, drained[0].Kind);
        Assert.Equal(ConnectionEventKind.FrameReceived, drained[1].Kind);
        Assert.Equal(ConnectionEventKind.Closed, drained[2].Kind);
    }

    [Fact]
    public void IngressFull_NeverOverwritesValidatedFrame()
    {
        var factory = new ClientConnectionFactory();
        ClientConnectionCreateResult created = factory.Create(new ClientConnectionCreateRequest(1, 1), out IClientConnection connection);
        connection.Start();
        var buffer = new ConnectionEvent[8];
        connection.DrainEvents(buffer);
        Assert.True(created.Loopback.TryDeliverToClient(new EncodedFrame(new byte[] { 1, 2, 3 })));
        Assert.True(created.Loopback.TryDeliverToClient(new EncodedFrame(new byte[] { 9, 9, 9 })));
        int n = connection.DrainEvents(buffer);
        int frames = 0;
        byte first = 0;
        bool hasDisconnected = false;
        for (int i = 0; i < n; i++)
        {
            if (buffer[i].Kind == ConnectionEventKind.FrameReceived)
            {
                frames++;
                if (frames == 1)
                {
                    first = buffer[i].Frame.Bytes.Span[0];
                }
            }
            else if (buffer[i].Kind == ConnectionEventKind.Disconnected)
            {
                hasDisconnected = true;
            }
        }

        Assert.Equal(1, frames);
        Assert.Equal(1, first);
        Assert.True(hasDisconnected);
    }

    [Fact]
    public void EgressFull_ReturnsBeforeBlocking()
    {
        var queue = new ConnectionSendQueue(1);
        Assert.True(queue.TryEnqueue(new EncodedFrame(new byte[] { 1 })));
        Assert.False(queue.TryEnqueue(new EncodedFrame(new byte[] { 2 })));
    }

    [Fact]
    public void FactoryPath_EgressFull_DoesNotOverwriteFirstFrame()
    {
        var factory = new ClientConnectionFactory();
        ClientConnectionCreateResult created = factory.Create(new ClientConnectionCreateRequest(1, 1), out IClientConnection connection);
        connection.Start();
        Assert.True(connection.TrySend(new EncodedFrame(new byte[] { 1 })).Accepted);
        bool laterAccepted = true;
        for (byte i = 2; i <= 8; i++)
        {
            if (!connection.TrySend(new EncodedFrame(new byte[] { i })).Accepted)
            {
                laterAccepted = false;
            }
        }

        Assert.False(laterAccepted);
        Assert.True(created.Loopback.TryReceiveFromClient(out EncodedFrame first));
        Assert.Equal(1, first.Bytes.Span[0]);
    }
}
