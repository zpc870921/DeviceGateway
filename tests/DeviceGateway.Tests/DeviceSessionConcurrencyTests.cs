using DeviceGateway.Server.Registry;
using DeviceGateway.Server.Sessions;
using DeviceGateway.Shared.Protocol;
using DeviceGateway.Tests.Support;
using SuperSocket.ProtoBase;

namespace DeviceGateway.Tests;

/// <summary>
/// 会话与注册表绑定的并发安全测试。
/// <para>
/// 关注两个点：
/// ① <c>TryBindDevice</c> 在并发调用下不会把两个不同的 deviceId 绑到同一个会话；
/// ② 绑定设备后，跨设备消息能通过注册表找到这个会话。
/// </para>
/// </summary>
public class DeviceSessionConcurrencyTests
{
    /// <summary>
    /// 构造一个测试用的设备会话。
    /// </summary>
    private static DeviceSession CreateSession(DeviceRegistry registry)
    {
        return new DeviceSession(new DeviceFrameEncoder(), registry);
    }

    /// <summary>
    /// 并发调用 <c>TryBindDevice</c>，断言只有第一个 deviceId 绑定成功，
    /// 其余全部失败 —— 不能出现"后到的把先到的覆盖掉"的竞态。
    /// </summary>
    [Fact]
    public async Task TryBindDevice_ConcurrentCalls_BindExactlyOneDeviceId()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);

        const int attempts = 64;

        var results = new bool[attempts];
        var deviceIds = new string[attempts];

        // 用独立线程而不是 Task.Run ——
        // 线程池注入新线程的速度很慢（每秒才一两个），
        // 64 个任务在 Barrier 上互相等，整个测试要等几十秒。
        // 专用线程能真正同时就位。
        ConcurrentRunner.Run(attempts, i =>
        {
            var id = $"DEVICE-{i:D3}";
            deviceIds[i] = id;

            results[i] = session.TryBindDevice(id);
        });

        await Task.CompletedTask;

        // 只有一个成功
        var successCount = results.Count(r => r);
        Assert.Equal(1, successCount);

        // 绑定的必须是那个"赢的" deviceId，且与会话当前值一致
        var boundIndex = Array.IndexOf(results, true);
        Assert.Equal(deviceIds[boundIndex], session.DeviceId);

        // 其它 deviceId 再试一次仍然失败（不能被后来的调用改写）
        for (var i = 0; i < attempts; i++)
        {
            if (i == boundIndex)
            {
                continue;
            }

            Assert.False(session.TryBindDevice(deviceIds[i]));
        }

        Assert.Equal(deviceIds[boundIndex], session.DeviceId);
    }

    /// <summary>
    /// 同一个 deviceId 并发绑定（正常重连场景）应当全部成功，
    /// 且最终绑定值不变。
    /// </summary>
    [Fact]
    public async Task TryBindDevice_ConcurrentSameDeviceId_AllSucceed()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);

        const int attempts = 32;
        var results = new bool[attempts];

        ConcurrentRunner.Run(attempts, i => { results[i] = session.TryBindDevice("DRONE-001"); });

        await Task.CompletedTask;

        Assert.All(results, r => Assert.True(r));
        Assert.Equal("DRONE-001", session.DeviceId);
    }

    /// <summary>
    /// 绑定后必须能通过注册表找到会话 —— 否则跨设备消息发不出去。
    /// 这条锁定了"只改内存 DeviceId 却没注册"的那种隐患。
    /// </summary>
    [Fact]
    public void Bind_AfterTryBindDevice_RegistryFindsSession()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);

        // 先在会话上绑定设备标识
        Assert.True(session.TryBindDevice("DRONE-001"));

        // 此时注册表里还没有 —— 会话标识与注册表绑定是两件事
        Assert.Null(registry.TryGetSession("DRONE-001"));

        // 注册表绑定之后才能查到
        registry.Bind("DRONE-001", session, DateTimeOffset.UtcNow);

        Assert.Same(session, registry.TryGetSession("DRONE-001"));
    }

    /// <summary>
    /// 多个会话并发绑定同一个 deviceId 时，注册表里最终只保留一个会话，
    /// 其余的被踢掉（重连场景）。
    /// </summary>
    [Fact]
    public async Task Bind_ConcurrentSessionsForSameDevice_OneWinner()
    {
        var registry = new DeviceRegistry();

        var sessions = Enumerable.Range(0, 16)
            .Select(_ => CreateSession(registry))
            .ToArray();

        ConcurrentRunner.Run(sessions.Length, i =>
            registry.Bind("DRONE-001", sessions[i], DateTimeOffset.UtcNow));

        await Task.CompletedTask;

        // 注册表里只有一个绑定
        Assert.Equal(1, registry.OnlineCount);

        // 而且它确实是 sessions 里的某一个
        var bound = registry.TryGetSession("DRONE-001");
        Assert.NotNull(bound);
        Assert.Contains(bound!, sessions);
    }

    /// <summary>
    /// 并发发送必须串行 —— 同一会话上的多次发送不能字节交错。
    /// </summary>
    /// <remarks>
    /// 跨设备消息（HTTP 线程触发）与设备上报的响应可能同时写同一个会话。
    /// 没有发送锁的话，两次写会交错，接收方解析出来的帧就坏了。
    /// <para>
    /// 这里用 <c>SendSerializedAsync</c> 接缝注入一个会"慢一点"的假传输，
    /// 如果没有锁，多个包就会同时进入传输层。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SendSerializedAsync_ConcurrentCalls_DoNotInterleaveFrames()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);

        const int count = 32;

        var inFlight = 0;
        var maxInFlight = 0;
        var received = new List<DevicePackage>();

        var gate = new object();

        var transport = async (DevicePackage package, CancellationToken ct) =>
        {
            var current = Interlocked.Increment(ref inFlight);

            // 记录同一时刻有多少个发送在传输层里。
            lock (gate)
            {
                maxInFlight = Math.Max(maxInFlight, current);
            }

            // 稍微慢一点，让并发窗口打开。
            await Task.Delay(1, ct);

            lock (gate)
            {
                received.Add(package);
            }

            Interlocked.Decrement(ref inFlight);
        };

        var tasks = Enumerable.Range(0, count).Select(i => session.SendSerializedAsync(
            new DevicePackage
            {
                Key = DeviceMessageTypes.Report,
                Payload = $"{{\"n\":{i}}}"
            },
            transport,
            CancellationToken.None)).ToArray();

        await Task.WhenAll(tasks);

        // 所有包都发到了 —— 没有丢。
        Assert.Equal(count, received.Count);

        // 关键断言：任意时刻传输层里最多只有一个包。
        // 没有发送锁的话这里会大于 1。
        Assert.Equal(1, maxInFlight);
    }

    /// <summary>
    /// 关闭之后发送必须失败。
    /// </summary>
    [Fact]
    public async Task SendSerializedAsync_AfterClose_Throws()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);

        await session.CloseAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SendSerializedAsync(
                new DevicePackage { Key = DeviceMessageTypes.Report, Payload = "{}" },
                (_, _) => Task.CompletedTask,
                CancellationToken.None));
    }

    /// <summary>
    /// 发送必须尊重取消令牌。
    /// </summary>
    [Fact]
    public async Task SendSerializedAsync_HonorsCancellationToken()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.SendSerializedAsync(
                new DevicePackage { Key = DeviceMessageTypes.Report, Payload = "{}" },
                (_, _) => Task.CompletedTask,
                cts.Token));
    }
}
