using DeviceGateway.Server.Registry;
using DeviceGateway.Tests.Support;

namespace DeviceGateway.Tests;

/// <summary>
/// 设备注册表的测试。
/// <para>
/// 重点覆盖两个容易出错的地方：
/// 设备重连时旧会话的关闭事件不能误删新会话的绑定（比较删除）；
/// 离线判定只看心跳时间，不受其它消息影响。
/// </para>
/// </summary>
public class DeviceRegistryTests
{
    /// <summary>
    /// 绑定会话并刷新一次心跳 —— 让设备进入"在线"状态。
    /// </summary>
    /// <remarks>
    /// 在线状态只由心跳决定（见 <c>Bind</c> 的说明），
    /// 所以凡是测试"在线设备"的场景都要走这一步。
    /// </remarks>
    private static void BindOnline(
        DeviceRegistry registry,
        string deviceId,
        FakeSessionHandle session,
        DateTimeOffset now)
    {
        registry.Bind(deviceId, session, now);
        registry.TouchHeartbeat(deviceId, session, now);
    }

    // ------------------------------------------------------------------
    // 设备登记
    // ------------------------------------------------------------------

    /// <summary>
    /// 首次上报时设备不存在，应该被自动添加。
    /// </summary>
    [Fact]
    public void EnsureRegistered_AddsDeviceOnFirstReport()
    {
        var registry = new DeviceRegistry();

        Assert.Null(registry.TryGetDevice("DRONE-001"));

        var now = DateTimeOffset.UtcNow;
        registry.EnsureRegistered("DRONE-001", now);

        var record = registry.TryGetDevice("DRONE-001");
        Assert.NotNull(record);
        Assert.Equal("DRONE-001", record!.DeviceId);
        Assert.Equal(now, record.FirstSeenAt);
    }

    /// <summary>
    /// 重复登记同一台设备不会覆盖原有的首次出现时间。
    /// </summary>
    [Fact]
    public void EnsureRegistered_DoesNotResetFirstSeen()
    {
        var registry = new DeviceRegistry();
        var first = DateTimeOffset.UtcNow;

        registry.EnsureRegistered("DRONE-001", first);
        registry.EnsureRegistered("DRONE-001", first.AddHours(1));

        Assert.Equal(first, registry.TryGetDevice("DRONE-001")!.FirstSeenAt);
    }

    /// <summary>
    /// 登记空设备标识必须被拒绝 —— 那是无效输入，不是"自动修正"。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureRegistered_ThrowsOnEmptyDeviceId(string deviceId)
    {
        var registry = new DeviceRegistry();

        Assert.Throws<ArgumentException>(
            () => registry.EnsureRegistered(deviceId, DateTimeOffset.UtcNow));
    }

    // ------------------------------------------------------------------
    // 绑定
    // ------------------------------------------------------------------

    /// <summary>
    /// 绑定会话后应该能按 deviceId 找到它。
    /// </summary>
    [Fact]
    public void Bind_MakesSessionFindable()
    {
        var registry = new DeviceRegistry();
        var session = new FakeSessionHandle("s1", "DRONE-001");

        registry.Bind("DRONE-001", session, DateTimeOffset.UtcNow);

        Assert.Same(session, registry.TryGetSession("DRONE-001"));
        Assert.Equal(1, registry.OnlineCount);
    }

    /// <summary>
    /// 绑定会记录连接时间，但<b>不</b>改变在线状态。
    /// </summary>
    /// <remarks>
    /// 在线状态只由心跳决定。绑定只回答"这条连接属于哪台设备" ——
    /// 否则看门狗判离线之后，设备只要还在发位置数据就会被改回在线。
    /// </remarks>
    [Fact]
    public void Bind_RecordsConnectedAtButDoesNotChangeStatus()
    {
        var registry = new DeviceRegistry();
        var session = new FakeSessionHandle("s1", "DRONE-001");
        var now = DateTimeOffset.UtcNow;

        registry.Bind("DRONE-001", session, now);

        var record = registry.TryGetDevice("DRONE-001")!;
        Assert.Equal(now, record.ConnectedAt);

        // 还没有心跳，设备不算在线。
        Assert.Equal(DeviceStatus.Offline, record.Status);
    }

    /// <summary>
    /// 绑定之后收到心跳，设备才变成在线。
    /// </summary>
    [Fact]
    public void Bind_ThenTouchHeartbeat_BecomesOnline()
    {
        var registry = new DeviceRegistry();
        var session = new FakeSessionHandle("s1", "DRONE-001");
        var now = DateTimeOffset.UtcNow;

        registry.Bind("DRONE-001", session, now);

        Assert.Equal(DeviceStatus.Offline, registry.TryGetDevice("DRONE-001")!.Status);

        // 心跳到达 —— 离线判定只看这个。
        registry.TouchHeartbeat("DRONE-001", session, now);

        Assert.Equal(DeviceStatus.Online, registry.TryGetDevice("DRONE-001")!.Status);
    }

    /// <summary>
    /// 同一设备重连时，旧的连接要被踢掉 —— 否则会出现一个设备占着两条连接，
    /// 后面推消息给哪一条就不确定了。
    /// </summary>
    /// <remarks>
    /// <see cref="DeviceRegistry.Bind"/> 只<b>返回</b>被替换的旧会话，不自己关闭它 ——
    /// 关闭可能要等待网络操作，不能在注册表的锁里做。关闭由调用方负责
    /// （<see cref="DeviceRegistry.TryBind"/> 就是这么做的）。
    /// </remarks>
    [Fact]
    public void Bind_KicksOutStaleSessionOnReconnect()
    {
        var registry = new DeviceRegistry();
        var oldSession = new FakeSessionHandle("s1", "DRONE-001");
        var newSession = new FakeSessionHandle("s2", "DRONE-001");

        registry.Bind("DRONE-001", oldSession, DateTimeOffset.UtcNow);
        var stale = registry.Bind("DRONE-001", newSession, DateTimeOffset.UtcNow);

        // 新会话接管了绑定。
        Assert.Same(newSession, registry.TryGetSession("DRONE-001"));

        // 旧会话被返回，由调用方去关闭。
        Assert.Same(oldSession, stale);

        // Bind 本身不关闭 —— 关闭是调用方的事。
        Assert.Equal(0, oldSession.CloseCount);
    }

    /// <summary>
    /// 同一个会话重复绑定不返回陈旧会话，也不踢掉自己。
    /// </summary>
    [Fact]
    public void Bind_SameSessionTwice_ReturnsNullAndDoesNotEvict()
    {
        var registry = new DeviceRegistry();
        var session = new FakeSessionHandle("s1", "DRONE-001");

        registry.Bind("DRONE-001", session, DateTimeOffset.UtcNow);
        var stale = registry.Bind("DRONE-001", session, DateTimeOffset.UtcNow);

        Assert.Null(stale);
        Assert.Same(session, registry.TryGetSession("DRONE-001"));
        Assert.Equal(0, session.CloseCount);
    }

    // ------------------------------------------------------------------
    // 解绑
    // ------------------------------------------------------------------

    /// <summary>
    /// 关键场景：设备重连后，旧会话的关闭事件才姗姗来迟。
    /// 这时不能把新会话的绑定误删掉 —— 否则设备变成"在线但找不到"。
    /// </summary>
    [Fact]
    public void UnbindAndMarkOffline_DoesNotRemoveNewerSessionBinding()
    {
        var registry = new DeviceRegistry();
        var oldSession = new FakeSessionHandle("s1", "DRONE-001");
        var newSession = new FakeSessionHandle("s2", "DRONE-001");

        registry.Bind("DRONE-001", oldSession, DateTimeOffset.UtcNow);
        registry.Bind("DRONE-001", newSession, DateTimeOffset.UtcNow);

        // 旧会话的关闭事件到了 —— 这时绑定的是新会话。
        var removed = registry.UnbindAndMarkOffline("DRONE-001", oldSession);

        Assert.False(removed);

        // 新会话的绑定必须还在。
        Assert.Same(newSession, registry.TryGetSession("DRONE-001"));
        Assert.Equal(1, registry.OnlineCount);
    }

    /// <summary>
    /// 正常注销：解绑后应该找不到会话，设备转为离线。
    /// </summary>
    [Fact]
    public void UnbindAndMarkOffline_RemovesCurrentSession()
    {
        var registry = new DeviceRegistry();
        var session = new FakeSessionHandle("s1", "DRONE-001");

        registry.Bind("DRONE-001", session, DateTimeOffset.UtcNow);

        var removed = registry.UnbindAndMarkOffline("DRONE-001", session);

        Assert.True(removed);
        Assert.Null(registry.TryGetSession("DRONE-001"));
        Assert.Equal(0, registry.OnlineCount);
        Assert.Equal(DeviceStatus.Offline, registry.TryGetDevice("DRONE-001")!.Status);
    }

    /// <summary>
    /// 注销一个从来就没绑过的会话必须返回 false。
    /// </summary>
    [Fact]
    public void UnbindAndMarkOffline_ReturnsFalseForUnknownDevice()
    {
        var registry = new DeviceRegistry();
        var session = new FakeSessionHandle("s1", "DRONE-001");

        Assert.False(registry.UnbindAndMarkOffline("DRONE-001", session));
    }

    /// <summary>
    /// 注销一个已被替换掉的旧会话必须返回 false。
    /// </summary>
    [Fact]
    public void UnbindAndMarkOffline_ReturnsFalseForStaleSession()
    {
        var registry = new DeviceRegistry();
        var oldSession = new FakeSessionHandle("s1", "DRONE-001");
        var newSession = new FakeSessionHandle("s2", "DRONE-001");

        registry.Bind("DRONE-001", oldSession, DateTimeOffset.UtcNow);
        registry.Bind("DRONE-001", newSession, DateTimeOffset.UtcNow);

        Assert.False(registry.UnbindAndMarkOffline("DRONE-001", oldSession));
        Assert.Same(newSession, registry.TryGetSession("DRONE-001"));
    }

    // ------------------------------------------------------------------
    // 心跳
    // ------------------------------------------------------------------

    /// <summary>
    /// 刷新心跳会更新最后心跳时间，并保持在线状态。
    /// </summary>
    [Fact]
    public void TouchHeartbeat_RefreshesTimestampAndKeepsOnline()
    {
        var registry = new DeviceRegistry();
        var session = new FakeSessionHandle("s1", "DRONE-001");

        var bindTime = DateTimeOffset.UtcNow;
        registry.Bind("DRONE-001", session, bindTime);

        var beatTime = bindTime.AddSeconds(30);
        var success = registry.TouchHeartbeat("DRONE-001", session, beatTime);

        Assert.True(success);

        var record = registry.TryGetDevice("DRONE-001")!;
        Assert.Equal(beatTime, record.LastHeartbeatAt);
        Assert.Equal(DeviceStatus.Online, record.Status);
    }

    /// <summary>
    /// 只有当前绑定的会话才能刷新心跳 —— 旧连接不能影响新连接的状态。
    /// </summary>
    [Fact]
    public void TouchHeartbeat_OnlyCurrentSessionCanRefresh()
    {
        var registry = new DeviceRegistry();
        var oldSession = new FakeSessionHandle("s1", "DRONE-001");
        var newSession = new FakeSessionHandle("s2", "DRONE-001");

        var bindTime = DateTimeOffset.UtcNow;
        registry.Bind("DRONE-001", oldSession, bindTime);
        registry.Bind("DRONE-001", newSession, bindTime);

        // 旧会话刷新心跳 —— 必须被拒绝。
        var success = registry.TouchHeartbeat("DRONE-001", oldSession, bindTime.AddSeconds(30));

        Assert.False(success);
    }

    /// <summary>
    /// 未绑定的设备不能刷新心跳。
    /// </summary>
    [Fact]
    public void TouchHeartbeat_RejectedForUnboundDevice()
    {
        var registry = new DeviceRegistry();
        var session = new FakeSessionHandle("s1", "DRONE-001");

        var success = registry.TouchHeartbeat("DRONE-001", session, DateTimeOffset.UtcNow);

        Assert.False(success);
    }

    /// <summary>
    /// 心跳时间不能倒退 —— 时钟回拨或乱序到达都不该把最后心跳时间改早。
    /// </summary>
    [Fact]
    public void TouchHeartbeat_DoesNotMoveTimeBackwards()
    {
        var registry = new DeviceRegistry();
        var session = new FakeSessionHandle("s1", "DRONE-001");

        var bindTime = DateTimeOffset.UtcNow;
        registry.Bind("DRONE-001", session, bindTime);

        // 传一个比绑定时间更早的时间戳。
        registry.TouchHeartbeat("DRONE-001", session, bindTime.AddSeconds(-10));

        Assert.Equal(bindTime, registry.TryGetDevice("DRONE-001")!.LastHeartbeatAt);
    }

    // ------------------------------------------------------------------
    // 当前会话校验
    // ------------------------------------------------------------------

    /// <summary>
    /// 当前绑定的会话通过校验。
    /// </summary>
    [Fact]
    public void IsCurrentSession_TrueForBoundSession()
    {
        var registry = new DeviceRegistry();
        var session = new FakeSessionHandle("s1", "DRONE-001");

        registry.Bind("DRONE-001", session, DateTimeOffset.UtcNow);

        Assert.True(registry.IsCurrentSession("DRONE-001", session));
    }

    /// <summary>
    /// 被替换掉的旧会话不通过校验。
    /// </summary>
    [Fact]
    public void IsCurrentSession_FalseForStaleSession()
    {
        var registry = new DeviceRegistry();
        var oldSession = new FakeSessionHandle("s1", "DRONE-001");
        var newSession = new FakeSessionHandle("s2", "DRONE-001");

        registry.Bind("DRONE-001", oldSession, DateTimeOffset.UtcNow);
        registry.Bind("DRONE-001", newSession, DateTimeOffset.UtcNow);

        Assert.False(registry.IsCurrentSession("DRONE-001", oldSession));
        Assert.True(registry.IsCurrentSession("DRONE-001", newSession));
    }

    /// <summary>
    /// 当前会话校验<b>不</b>刷新心跳、<b>不</b>改变在线状态。
    /// </summary>
    /// <remarks>
    /// 这条是「心跳超时判离线」这条需求的关键：
    /// 位置数据每秒钟来一次，如果它能刷新心跳，
    /// 心跳停了的设备就永远判不出离线。
    /// </remarks>
    [Fact]
    public void IsCurrentSession_DoesNotModifyHeartbeatOrStatus()
    {
        var registry = new DeviceRegistry();
        var session = new FakeSessionHandle("s1", "DRONE-001");

        var bindTime = DateTimeOffset.UtcNow;
        BindOnline(registry, "DRONE-001", session, bindTime);

        var laterTime = bindTime.AddSeconds(30);

        registry.IsCurrentSession("DRONE-001", session);

        var record = registry.TryGetDevice("DRONE-001")!;
        Assert.Equal(bindTime, record.LastHeartbeatAt);
        Assert.Equal(DeviceStatus.Online, record.Status);
    }

    // ------------------------------------------------------------------
    // 心跳超时
    // ------------------------------------------------------------------

    /// <summary>
    /// 心跳超时判定：超过阈值才判离线，在阈值内不算离线。
    /// </summary>
    [Fact]
    public void FindHeartbeatExpired_OnlyReturnsDevicesPastTimeout()
    {
        var registry = new DeviceRegistry();
        var now = DateTimeOffset.UtcNow;

        // fresh：刚绑上，心跳是新的
        var freshSession = new FakeSessionHandle("s1", "FRESH");
        BindOnline(registry, "FRESH", freshSession, now);

        // stale：心跳很久以前
        var staleSession = new FakeSessionHandle("s2", "STALE");
        BindOnline(registry, "STALE", staleSession, now.AddMinutes(-10));

        var expired = registry.FindHeartbeatExpired(now, TimeSpan.FromSeconds(90));

        Assert.Single(expired);
        Assert.Equal("STALE", expired[0].DeviceId);
    }

    /// <summary>
    /// 离线判定只看心跳时间 —— 设备还在发别的数据也不会被算成在线。
    /// 这是"心跳超时判离线"的核心语义。
    /// </summary>
    [Fact]
    public void FindHeartbeatExpired_IgnoresNonHeartbeatActivity()
    {
        var registry = new DeviceRegistry();
        var now = DateTimeOffset.UtcNow;

        // 心跳是 10 分钟前。
        var session = new FakeSessionHandle("s1", "DRONE-001");
        BindOnline(registry, "DRONE-001", session, now.AddMinutes(-10));

        // 中间收到过位置数据 —— 但那不刷新心跳时间（IsCurrentSession 不碰心跳）。
        registry.IsCurrentSession("DRONE-001", session);

        var expired = registry.FindHeartbeatExpired(now, TimeSpan.FromSeconds(90));

        Assert.Single(expired);
        Assert.Equal("DRONE-001", expired[0].DeviceId);
    }

    /// <summary>
    /// 已经离线的设备不应被反复报出来 —— 否则看门狗每轮都会重复告警。
    /// </summary>
    [Fact]
    public void FindHeartbeatExpired_SkipsAlreadyOfflineDevices()
    {
        var registry = new DeviceRegistry();
        var now = DateTimeOffset.UtcNow;

        var session = new FakeSessionHandle("s1", "DRONE-001");
        BindOnline(registry, "DRONE-001", session, now.AddMinutes(-10));

        // 断开连接 ⇒ 立即离线。
        registry.UnbindAndMarkOffline("DRONE-001", session);

        var expired = registry.FindHeartbeatExpired(now, TimeSpan.FromSeconds(90));

        Assert.Empty(expired);
    }

    /// <summary>
    /// <see cref="DeviceRegistry.FindHeartbeatExpired"/> 只查询，不改状态。
    /// </summary>
    [Fact]
    public void FindHeartbeatExpired_DoesNotMutateState()
    {
        var registry = new DeviceRegistry();
        var now = DateTimeOffset.UtcNow;

        var session = new FakeSessionHandle("s1", "DRONE-001");
        BindOnline(registry, "DRONE-001", session, now.AddMinutes(-10));

        registry.FindHeartbeatExpired(now, TimeSpan.FromSeconds(90));

        // 状态没变，会话还在 —— 真正改变状态的是 ExpireHeartbeatSessions。
        Assert.Equal(DeviceStatus.Online, registry.TryGetDevice("DRONE-001")!.Status);
        Assert.Equal(1, registry.OnlineCount);
    }

    /// <summary>
    /// 超时处理：标记离线、移除会话，并把会话交给调用方去关闭。
    /// </summary>
    [Fact]
    public void ExpireHeartbeatSessions_MarksOfflineAndReturnsSession()
    {
        var registry = new DeviceRegistry();
        var now = DateTimeOffset.UtcNow;

        var session = new FakeSessionHandle("s1", "DRONE-001");
        BindOnline(registry, "DRONE-001", session, now.AddMinutes(-10));

        var expired = registry.ExpireHeartbeatSessions(now, TimeSpan.FromSeconds(90));

        Assert.Single(expired);
        Assert.Same(session, expired[0]);

        Assert.Null(registry.TryGetSession("DRONE-001"));
        Assert.Equal(0, registry.OnlineCount);
        Assert.Equal(DeviceStatus.Offline, registry.TryGetDevice("DRONE-001")!.Status);
    }

    /// <summary>
    /// 没超时的设备不受影响。
    /// </summary>
    [Fact]
    public void ExpireHeartbeatSessions_NotExpired_DoesNothing()
    {
        var registry = new DeviceRegistry();
        var now = DateTimeOffset.UtcNow;

        var session = new FakeSessionHandle("s1", "DRONE-001");
        BindOnline(registry, "DRONE-001", session, now);

        var expired = registry.ExpireHeartbeatSessions(now, TimeSpan.FromSeconds(90));

        Assert.Empty(expired);
        Assert.Same(session, registry.TryGetSession("DRONE-001"));
        Assert.Equal(DeviceStatus.Online, registry.TryGetDevice("DRONE-001")!.Status);
    }

    /// <summary>
    /// 超时处理是幂等的 —— 第二轮不会重复报同一台设备。
    /// </summary>
    [Fact]
    public void ExpireHeartbeatSessions_IsIdempotent()
    {
        var registry = new DeviceRegistry();
        var now = DateTimeOffset.UtcNow;

        var session = new FakeSessionHandle("s1", "DRONE-001");
        BindOnline(registry, "DRONE-001", session, now.AddMinutes(-10));

        var first = registry.ExpireHeartbeatSessions(now, TimeSpan.FromSeconds(90));
        var second = registry.ExpireHeartbeatSessions(now, TimeSpan.FromSeconds(90));

        Assert.Single(first);
        Assert.Empty(second);
    }

    // ------------------------------------------------------------------
    // 设备档案
    // ------------------------------------------------------------------

    /// <summary>
    /// 离线设备仍然保留在设备列表里（能查到它是谁，只是状态是离线）。
    /// </summary>
    [Fact]
    public void DeviceRecord_SurvivesDisconnect()
    {
        var registry = new DeviceRegistry();
        var session = new FakeSessionHandle("s1", "DRONE-001");

        registry.Bind("DRONE-001", session, DateTimeOffset.UtcNow);
        registry.UnbindAndMarkOffline("DRONE-001", session);

        // 会话没了，但设备档案还在。
        Assert.Null(registry.TryGetSession("DRONE-001"));
        Assert.NotNull(registry.TryGetDevice("DRONE-001"));
        Assert.Equal(DeviceStatus.Offline, registry.TryGetDevice("DRONE-001")!.Status);
    }

    /// <summary>
    /// 查不存在的设备时返回 null，而不是抛异常。
    /// </summary>
    [Theory]
    [InlineData("NOT-EXIST")]
    [InlineData("")]
    [InlineData(null)]
    public void TryGet_ReturnsNullForUnknownDevice(string? deviceId)
    {
        var registry = new DeviceRegistry();

        Assert.Null(registry.TryGetDevice(deviceId));
        Assert.Null(registry.TryGetSession(deviceId));
    }
}
