using Lixin.Core.Connection;
using Lixin.Core.Transport;

namespace Lixin.Tests;

/// <summary>
/// 监督者状态机：全部用假通道 + 假延迟，不碰蓝牙。
/// 事件序列（Connected / Disconnected 恰好各一次）与退避节奏是钉死的目标。
/// </summary>
public class ConnectionSupervisorTests
{
    /// <summary>可关闭的假通道，记录 CloseAsync 调用次数。</summary>
    private sealed class FakeChannel : IByteChannel
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "fake";
        public bool IsOpen { get; private set; } = true;
        public int CloseCount { get; private set; }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // 监督者不是读者（design-t3.3 §1），这里没人调用；真被调到就当作已关闭。
            return ValueTask.FromResult(0);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public Task CloseAsync()
        {
            if (IsOpen)
            {
                IsOpen = false;
                CloseCount++;
                _closed.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            CloseAsync();
            return ValueTask.CompletedTask;
        }

        public Task WaitClosedAsync() => _closed.Task;
    }

    /// <summary>按脚本出牌的工厂：依次成功/抛异常；记录调用次数与最近创建的通道。</summary>
    private sealed class ScriptedFactory
    {
        private readonly Queue<Exception?> _script;

        public int Calls { get; private set; }

        public FakeChannel? LastCreated { get; private set; }

        /// <param name="script">按顺序出牌：null = 成功（新建假通道），异常 = 该次尝试失败。耗尽后默认成功。</param>
        public ScriptedFactory(params Exception?[] script) => _script = new Queue<Exception?>(script);

        public Task<IByteChannel> Connect(CancellationToken cancellationToken)
        {
            Calls++;
            if (_script.Count == 0)
            {
                return Task.FromResult<IByteChannel>(CreateChannel());
            }

            var exception = _script.Dequeue();
            if (exception is null)
            {
                return Task.FromResult<IByteChannel>(CreateChannel());
            }

            return Task.FromException<IByteChannel>(exception);
        }

        private FakeChannel CreateChannel()
        {
            var channel = new FakeChannel();
            LastCreated = channel;
            return channel;
        }
    }

    /// <summary>记录假延迟的实际取值，断言退避序列真的被走了一遍。</summary>
    private sealed class FakeDelayer
    {
        public List<int> Delays { get; } = new();

        public Task Delay(int ms, CancellationToken cancellationToken)
        {
            Delays.Add(ms);
            return Task.CompletedTask;
        }
    }

    private static async Task<T> WaitAsync<T>(TaskCompletionSource<T> source, int timeoutMs = 3000)
    {
        var winner = await Task.WhenAny(source.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        Assert.True(winner == source.Task, $"等待事件超时（{timeoutMs} ms）——状态机没有走到预期步骤。");
        return await source.Task.ConfigureAwait(false);
    }

    private static async Task WaitIdleAsync(int timeoutMs = 100)
    {
        // 给状态机一个跑完同步步骤的窗口（假延迟不真实等待）。
        await Task.Delay(timeoutMs);
    }

    [Fact]
    public async Task Start_FirstConnectSucceeds_ConnectedFires_NoBackoff()
    {
        var factory = new ScriptedFactory();
        var delayer = new FakeDelayer();
        var supervisor = new ConnectionSupervisor(factory.Connect, new ConnectionSupervisorOptions
        {
            DelayAsync = delayer.Delay,
        });

        var connected = new TaskCompletionSource<IByteChannel>();
        supervisor.Connected += (_, channel) => connected.TrySetResult(channel);

        await supervisor.StartAsync();
        var channel = await WaitAsync(connected);

        Assert.Same(channel, supervisor.Current);
        Assert.True(supervisor.IsRunning);
        Assert.Equal(0, supervisor.ConsecutiveFailures);
        Assert.Empty(delayer.Delays);              // 首连不走退避
        Assert.Equal(1, factory.Calls);

        await supervisor.StopAsync();
    }

    [Fact]
    public async Task LinkLost_ThenReconnect_EventsFire_OldChannelClosed()
    {
        var factory = new ScriptedFactory();
        var delayer = new FakeDelayer();
        var supervisor = new ConnectionSupervisor(factory.Connect, new ConnectionSupervisorOptions
        {
            DelayAsync = delayer.Delay,
        });

        var connections = new List<IByteChannel>();
        var disconnected = new TaskCompletionSource<object?>();
        supervisor.Connected += (_, channel) => connections.Add(channel);
        supervisor.Disconnected += (_, _) => disconnected.TrySetResult(null);

        await supervisor.StartAsync();
        await WaitIdleAsync();
        Assert.Single(connections);
        var first = connections[0];

        supervisor.ReportLinkLost(first);
        await WaitAsync(disconnected);

        // 重连成功（假延迟立即返回，退避序列真实走过但瞬间完成）。
        await WaitIdleAsync();
        Assert.Equal(2, connections.Count);
        Assert.NotSame(first, connections[1]);
        Assert.Same(connections[1], supervisor.Current);
        Assert.Equal(0, supervisor.ConsecutiveFailures);   // 成功即归零
        Assert.Equal(1, Assert.IsType<FakeChannel>(first).CloseCount);   // 旧通道被关闭
        Assert.Empty(delayer.Delays);                      // 没失败过，不该有退避

        await supervisor.StopAsync();
    }

    [Fact]
    public async Task RepeatedFailures_UseBackoffSequence_ThenRecover()
    {
        // 脚本：失败、失败、成功。
        var factory = new ScriptedFactory(
            new IOException("attempt 1 fails"),
            new IOException("attempt 2 fails"));
        var delayer = new FakeDelayer();
        var supervisor = new ConnectionSupervisor(factory.Connect, new ConnectionSupervisorOptions
        {
            DelayAsync = delayer.Delay,
        });

        var connected = new TaskCompletionSource<IByteChannel>();
        supervisor.Connected += (_, channel) => connected.TrySetResult(channel);

        await supervisor.StartAsync();
        await WaitAsync(connected);

        // 两次失败走了两档退避；成功后计数归零。
        Assert.Equal(new[] { 1000, 2000 }, delayer.Delays);
        Assert.Equal(3, factory.Calls);
        Assert.Equal(0, supervisor.ConsecutiveFailures);

        await supervisor.StopAsync();
    }

    [Fact]
    public async Task ReportLinkLost_WrongChannel_IsIgnored()
    {
        var factory = new ScriptedFactory();
        var supervisor = new ConnectionSupervisor(factory.Connect, new ConnectionSupervisorOptions());

        var connected = new TaskCompletionSource<IByteChannel>();
        var disconnected = new TaskCompletionSource<object?>();
        supervisor.Connected += (_, channel) => connected.TrySetResult(channel);
        supervisor.Disconnected += (_, _) => disconnected.TrySetResult(null);

        await supervisor.StartAsync();
        var current = await WaitAsync(connected);

        // 迟到的旧通道事件不该触发断开。
        supervisor.ReportLinkLost(new FakeChannel());
        await WaitIdleAsync();
        Assert.False(disconnected.Task.IsCompleted);
        Assert.Same(current, supervisor.Current);

        // 当前通道上报才生效。
        supervisor.ReportLinkLost(current);
        await WaitAsync(disconnected);

        await supervisor.StopAsync();
    }

    [Fact]
    public async Task ReportLinkLost_NullChannel_TreatedAsCurrent()
    {
        // 上游只知道"断了"不知道是哪条链时，传 null 允许偷懒（design-t3.3 §1 的易用性让步）。
        var factory = new ScriptedFactory();
        var supervisor = new ConnectionSupervisor(factory.Connect, new ConnectionSupervisorOptions());

        var connected = new TaskCompletionSource<IByteChannel>();
        var disconnected = new TaskCompletionSource<object?>();
        supervisor.Connected += (_, channel) => connected.TrySetResult(channel);
        supervisor.Disconnected += (_, _) => disconnected.TrySetResult(null);

        await supervisor.StartAsync();
        await WaitAsync(connected);

        supervisor.ReportLinkLost(null);
        await WaitAsync(disconnected);

        await supervisor.StopAsync();
    }

    [Fact]
    public async Task StopAsync_ClosesCurrent_StopsLoop()
    {
        var factory = new ScriptedFactory();
        var supervisor = new ConnectionSupervisor(factory.Connect, new ConnectionSupervisorOptions());

        var connected = new TaskCompletionSource<IByteChannel>();
        supervisor.Connected += (_, channel) => connected.TrySetResult(channel);

        await supervisor.StartAsync();
        var channel = await WaitAsync(connected);

        await supervisor.StopAsync();

        Assert.False(supervisor.IsRunning);
        Assert.Null(supervisor.Current);
        Assert.Equal(1, Assert.IsType<FakeChannel>(channel).CloseCount);

        // 停止后循环不再发起连接。
        var callsAtStop = factory.Calls;
        await WaitIdleAsync(200);
        Assert.Equal(callsAtStop, factory.Calls);
    }
}
