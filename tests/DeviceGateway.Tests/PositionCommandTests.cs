using DeviceGateway.Server.Commands;
using DeviceGateway.Server.Registry;
using DeviceGateway.Server.Sessions;
using DeviceGateway.Shared.Protocol;

namespace DeviceGateway.Tests;

/// <summary>
/// 位置命令的测试。
///
/// <para>
/// 这一组用例锁的是<b>需求</b>而不是实现细节：
/// 「位置数据不参与在线状态判定，离线只看心跳」。
/// </para>
///
/// <para>
/// 为什么这条值得单独用一组用例锁死：
/// 设备每秒发一包位置数据，心跳 30 秒一包。
/// 如果位置数据也刷新心跳时间，那么心跳停了的设备只要还在发位置数据，
/// 就永远判不出离线 —— 离线检测形同虚设。
/// </para>
/// </summary>
public class PositionCommandTests
{
    /// <summary>
    /// 构造一个测试用的设备会话。
    /// </summary>
    private static DeviceSession CreateSession(DeviceRegistry registry)
    {
        return new DeviceSession(new DeviceFrameEncoder(), registry);
    }

    /// <summary>
    /// 构造一条位置数据消息。
    /// </summary>
    private static DevicePackage BuildPositionPackage(string deviceId)
    {
        return new DevicePackage
        {
            Key = DeviceMessageTypes.Position,
            Payload = $$"""{"deviceId":"{{deviceId}}","lat":30.1,"lng":120.2}"""
        };
    }

    /// <summary>
    /// 位置数据<b>不</b>刷新心跳时间。
    /// </summary>
    /// <remarks>
    /// 这是本组最核心的断言。修复之前，位置命令会调用
    /// <c>TouchHeartbeat</c>，从而刷新最后心跳时间。
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_DoesNotRefreshHeartbeat()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);
        var command = new PositionCommand(registry);

        // 先让设备上线，绑定时的 LastHeartbeatAt 就是基准。
        await command.ExecuteAsync(session, BuildPositionPackage("DRONE-001"), CancellationToken.None);

        var afterBind = registry.TryGetDevice("DRONE-001")!.LastHeartbeatAt;

        // 等一小会儿，确保"现在"确实比基准晚。
        await Task.Delay(50);

        // 再来一包位置数据。
        await command.ExecuteAsync(session, BuildPositionPackage("DRONE-001"), CancellationToken.None);

        // 心跳时间必须原封不动。
        Assert.Equal(afterBind, registry.TryGetDevice("DRONE-001")!.LastHeartbeatAt);
    }

    /// <summary>
    /// 位置数据<b>不</b>把离线的设备改回在线。
    /// </summary>
    /// <remarks>
    /// 设备心跳超时被看门狗判离线之后，即使它还在每秒发位置数据，
    /// 也必须保持离线。否则看门狗每轮扫描都会把它标记为离线、
    /// 紧接着又被改回在线，状态反复横跳。
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_DoesNotRestoreOnlineStatus()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);
        var command = new PositionCommand(registry);

        // 先发一次心跳，让设备上线 —— 否则它从一开始就没在线过。
        await registry.TryBind(session, "DRONE-001", DateTimeOffset.UtcNow);
        registry.TouchHeartbeat("DRONE-001", session, DateTimeOffset.UtcNow);

        Assert.Equal(DeviceStatus.Online, registry.TryGetDevice("DRONE-001")!.Status);

        // 看门狗判它离线。
        var expired = registry.ExpireHeartbeatSessions(
            DateTimeOffset.UtcNow.AddMinutes(10),
            TimeSpan.FromSeconds(90));

        Assert.Single(expired);
        Assert.Equal(DeviceStatus.Offline, registry.TryGetDevice("DRONE-001")!.Status);

        // 离线之后设备还在发位置数据。
        await command.ExecuteAsync(session, BuildPositionPackage("DRONE-001"), CancellationToken.None);

        // 必须仍然是离线。
        Assert.Equal(DeviceStatus.Offline, registry.TryGetDevice("DRONE-001")!.Status);
    }

    /// <summary>
    /// 只发位置数据、从不发心跳的设备，永远不会被判成在线。
    /// </summary>
    /// <remarks>
    /// 这条把前面的断言串成一个完整的场景 ——
    /// 它就是「离线只看心跳」这条需求在位置数据高频到达时的样子：
    /// 只要心跳不来，这个设备在接口里就一直显示离线，
    /// 哪怕它每秒都在发位置数据。
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_DeviceWithPositionButNoHeartbeat_NeverBecomesOnline()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);
        var command = new PositionCommand(registry);

        // 设备上线，只发位置数据，从不发心跳。
        await command.ExecuteAsync(session, BuildPositionPackage("DRONE-001"), CancellationToken.None);

        // 持续发位置数据。
        for (var i = 0; i < 5; i++)
        {
            await command.ExecuteAsync(session, BuildPositionPackage("DRONE-001"), CancellationToken.None);
            await Task.Delay(10);
        }

        // 设备档案存在（自动添加），但状态始终是离线 ——
        // 心跳从来没来过，在线判定就永远不成立。
        var record = registry.TryGetDevice("DRONE-001")!;
        Assert.Equal(DeviceStatus.Offline, record.Status);

        // 看门狗也永远不会把它当成"需要判离线的在线设备" ——
        // 它本来就是离线的。
        var expired = registry.ExpireHeartbeatSessions(
            DateTimeOffset.UtcNow.AddSeconds(91),
            TimeSpan.FromSeconds(90));

        Assert.Empty(expired);
    }

    /// <summary>
    /// 心跳停了的设备，即使一直在发位置数据，也会被看门狗判离线。
    /// </summary>
    /// <remarks>
    /// 这是「心跳超时判离线」这条需求最核心的场景：
    /// 设备先正常在线（有心跳），然后心跳停了，但位置数据还在每秒发。
    /// 这时候必须被判离线 —— 修复之前位置数据会顺带刷新心跳，
    /// 这个设备就永远判不出离线了。
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_DeviceWhoseHeartbeatStopped_IsJudgedOfflineDespitePositionTraffic()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);
        var command = new PositionCommand(registry);

        // 设备上线并发过心跳 —— 在线。
        await registry.TryBind(session, "DRONE-001", DateTimeOffset.UtcNow);
        registry.TouchHeartbeat("DRONE-001", session, DateTimeOffset.UtcNow);

        Assert.Equal(DeviceStatus.Online, registry.TryGetDevice("DRONE-001")!.Status);

        // 心跳停了，但位置数据还在每秒发。
        for (var i = 0; i < 5; i++)
        {
            await command.ExecuteAsync(session, BuildPositionPackage("DRONE-001"), CancellationToken.None);
            await Task.Delay(10);
        }

        // 心跳时间没有被位置数据刷新 —— 仍然是最初那一次。
        var record = registry.TryGetDevice("DRONE-001")!;
        Assert.True(
            DateTimeOffset.UtcNow - record.LastHeartbeatAt < TimeSpan.FromSeconds(5),
            "位置数据不应该刷新心跳时间");

        // 90 秒之后看门狗扫描 —— 设备被判离线。
        var expired = registry.ExpireHeartbeatSessions(
            DateTimeOffset.UtcNow.AddSeconds(91),
            TimeSpan.FromSeconds(90));

        Assert.Single(expired);
        Assert.Equal(DeviceStatus.Offline, registry.TryGetDevice("DRONE-001")!.Status);
    }

    /// <summary>
    /// 首次位置上报会自动登记设备 —— 设备不存在就添加。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_RegistersDeviceOnFirstReport()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);
        var command = new PositionCommand(registry);

        Assert.Null(registry.TryGetDevice("DRONE-001"));

        await command.ExecuteAsync(session, BuildPositionPackage("DRONE-001"), CancellationToken.None);

        Assert.NotNull(registry.TryGetDevice("DRONE-001"));
        Assert.Same(session, registry.TryGetSession("DRONE-001"));
    }

    /// <summary>
    /// 负载里没有 deviceId 时忽略这条消息 —— 不能因此让连接崩掉。
    /// </summary>
    [Theory]
    [InlineData("""{"lat":30.1}""")]          // 没有 deviceId 字段
    [InlineData("""{"deviceId":""}""")]         // 空字符串
    [InlineData("""{"deviceId":123}""")]        // 不是字符串
    [InlineData("not json at all")]              // 根本不是 JSON
    [InlineData("")]
    public async Task ExecuteAsync_IgnoresPayloadWithoutDeviceId(string payload)
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);
        var command = new PositionCommand(registry);

        // 不抛异常就是通过。
        await command.ExecuteAsync(
            session,
            new DevicePackage { Key = DeviceMessageTypes.Position, Payload = payload },
            CancellationToken.None);

        // 也没有登记任何设备。
        Assert.Empty(registry.AllDevices);
    }

    /// <summary>
    /// 旧连接上的位置数据被忽略 —— 设备重连后，旧连接不该继续产生业务效果。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_RejectedForStaleSession()
    {
        var registry = new DeviceRegistry();
        var oldSession = CreateSession(registry);
        var newSession = CreateSession(registry);
        var command = new PositionCommand(registry);

        // 旧会话先上线。
        await command.ExecuteAsync(oldSession, BuildPositionPackage("DRONE-001"), CancellationToken.None);

        // 新会话接管（重连）。
        await command.ExecuteAsync(newSession, BuildPositionPackage("DRONE-001"), CancellationToken.None);

        Assert.Same(newSession, registry.TryGetSession("DRONE-001"));

        // 旧会话还在发位置数据 —— 应当被忽略，不能影响当前绑定。
        await command.ExecuteAsync(oldSession, BuildPositionPackage("DRONE-001"), CancellationToken.None);

        Assert.Same(newSession, registry.TryGetSession("DRONE-001"));
        Assert.Equal(1, registry.OnlineCount);
    }

    /// <summary>
    /// 会话声称的设备标识与已绑定的不一致时，消息被丢弃。
    /// </summary>
    /// <remarks>
    /// 这条是安全边界：任何客户端都不能在 JSON 里写别人的 deviceId
    /// 来劫持会话绑定。
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_RejectsMismatchedDeviceId()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);
        var command = new PositionCommand(registry);

        await command.ExecuteAsync(session, BuildPositionPackage("DRONE-001"), CancellationToken.None);

        // 同一会话声称自己是另一台设备。
        await command.ExecuteAsync(session, BuildPositionPackage("DRONE-002"), CancellationToken.None);

        // DRONE-002 不该被登记。
        Assert.Null(registry.TryGetDevice("DRONE-002"));
    }
}
