using DeviceGateway.Server.Registry;
using DeviceGateway.Server.Sessions;
using DeviceGateway.Shared.Protocol;

namespace DeviceGateway.Tests;

/// <summary>
/// 会话生命周期相关的测试：关闭语义、设备身份绑定、断开时的注销行为。
/// <para>
/// 这些用例只覆盖<b>不需要真实 socket</b> 的成员 ——
/// <c>DeviceSession</c> 继承自 <c>AppSession</c>，它的 <c>SendAsync</c> 需要真实连接，
/// 所以发送相关的用例走 <c>SendSerializedAsync</c> 接缝注入假传输。
/// </para>
/// </summary>
public class DeviceSessionLifecycleTests
{
    /// <summary>
    /// 构造一个测试用的设备会话。
    /// </summary>
    private static DeviceSession CreateSession(DeviceRegistry registry)
    {
        return new DeviceSession(new DeviceFrameEncoder(), registry);
    }

    /// <summary>
    /// 关闭之后会话标记为正在关闭。
    /// </summary>
    [Fact]
    public async Task CloseAsync_SetsIsClosing()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);

        Assert.False(session.IsClosing);

        await session.CloseAsync();

        Assert.True(session.IsClosing);
    }

    /// <summary>
    /// 重复关闭不能抛异常 —— 关闭回调和显式关闭可能都会触发。
    /// </summary>
    [Fact]
    public async Task CloseAsync_IsIdempotent()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);

        // 第一次
        await session.CloseAsync();

        // 第二次、第三次 —— 都不该抛
        await session.CloseAsync();
        await session.CloseAsync();

        Assert.True(session.IsClosing);
    }

    /// <summary>
    /// 关闭之后不能再绑定设备标识。
    /// </summary>
    [Fact]
    public async Task TryBindDevice_AfterClose_ReturnsFalse()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);

        await session.CloseAsync();

        Assert.False(session.TryBindDevice("DRONE-001"));
    }

    /// <summary>
    /// 关闭之后发送必须失败 —— 不能往一条已经关掉的连接上写。
    /// </summary>
    [Fact]
    public async Task SendPackageAsync_AfterClose_Throws()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);

        await session.CloseAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SendPackageAsync(
                new DevicePackage { Key = DeviceMessageTypes.Report, Payload = "{}" },
                CancellationToken.None));
    }

    /// <summary>
    /// 空设备标识必须被拒绝。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryBindDevice_RejectsEmptyDeviceId(string? deviceId)
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);

        Assert.False(session.TryBindDevice(deviceId!));
    }

    /// <summary>
    /// 首次绑定成功，之后只能用同一个设备标识。
    /// </summary>
    [Fact]
    public void TryBindDevice_RejectsDifferentDeviceIdAfterFirstBind()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);

        Assert.True(session.TryBindDevice("DRONE-001"));
        Assert.Equal("DRONE-001", session.DeviceId);

        // 同一个标识重复绑定 —— 成功。
        Assert.True(session.TryBindDevice("DRONE-001"));

        // 换一个标识 —— 拒绝。
        // 这条是安全边界：任何客户端都不能在 JSON 里写别人的 deviceId 来劫持会话绑定。
        Assert.False(session.TryBindDevice("DRONE-002"));
        Assert.Equal("DRONE-001", session.DeviceId);
    }

    /// <summary>
    /// 设备标识绑定之后不允许被改写。
    /// </summary>
    [Fact]
    public void DeviceId_CannotBeChangedAfterBinding()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);

        session.TryBindDevice("DRONE-001");

        for (var i = 0; i < 100; i++)
        {
            session.TryBindDevice($"OTHER-{i}");
        }

        Assert.Equal("DRONE-001", session.DeviceId);
    }

    /// <summary>
    /// 会话关闭时把自己从注册表注销掉，设备转为离线。
    /// </summary>
    /// <remarks>
    /// 这里直接调 <c>UnbindFromRegistry</c> 而不是等 <c>CloseAsync</c> ——
    /// <c>AppSession.CloseAsync()</c> 在没有真实连接时直接返回，
    /// <c>OnSessionClosedAsync</c> 根本不会触发。
    /// </remarks>
    [Fact]
    public void UnbindFromRegistry_RemovesSessionAndMarksDeviceOffline()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);

        session.TryBindDevice("DRONE-001");
        registry.Bind("DRONE-001", session, DateTimeOffset.UtcNow);

        session.UnbindFromRegistry();

        Assert.Null(registry.TryGetSession("DRONE-001"));
        Assert.Equal(0, registry.OnlineCount);
        Assert.Equal(DeviceStatus.Offline, registry.TryGetDevice("DRONE-001")!.Status);
    }

    /// <summary>
    /// 重连竞态：旧会话关闭时不能把新会话的绑定删掉。
    /// </summary>
    /// <remarks>
    /// 这是本组最重要的用例。设备重连后，旧连接的关闭事件姗姗来迟，
    /// 如果它把新会话的绑定删掉，设备就变成"在线但查不到"——
    /// 跨设备消息会发不出去。
    /// </remarks>
    [Fact]
    public void UnbindFromRegistry_DoesNotUnbindNewerSession()
    {
        var registry = new DeviceRegistry();

        var oldSession = CreateSession(registry);
        var newSession = CreateSession(registry);

        oldSession.TryBindDevice("DRONE-001");
        newSession.TryBindDevice("DRONE-001");

        var now = DateTimeOffset.UtcNow;

        // 旧会话先绑定，随后被新会话接管。
        registry.Bind("DRONE-001", oldSession, now);
        registry.Bind("DRONE-001", newSession, now);

        // 新会话的心跳到达 —— 设备在线。
        registry.TouchHeartbeat("DRONE-001", newSession, now);

        // 旧会话的关闭事件到了。
        oldSession.UnbindFromRegistry();

        // 新会话的绑定必须还在。
        Assert.Same(newSession, registry.TryGetSession("DRONE-001"));
        Assert.Equal(1, registry.OnlineCount);
        Assert.Equal(DeviceStatus.Online, registry.TryGetDevice("DRONE-001")!.Status);
    }

    /// <summary>
    /// 从没绑定过设备的会话关闭时不会出错。
    /// </summary>
    [Fact]
    public void UnbindFromRegistry_ForUnboundDevice_DoesNotThrow()
    {
        var registry = new DeviceRegistry();
        var session = CreateSession(registry);

        // 没有绑定设备标识就关闭 —— 不该抛异常。
        session.UnbindFromRegistry();

        Assert.Equal(0, registry.OnlineCount);
    }
}
