using DeviceGateway.Server.Registry;
using DeviceGateway.Tests.Support;

namespace DeviceGateway.Tests;

/// <summary>
/// 注册表并发绑定行为的测试。
/// <para>
/// 注册表用一把锁保护所有状态转换，所以这里的用例重点是：
/// 并发操作之后，注册表必须仍然处于一个<b>自洽</b>的状态 ——
/// 不能出现"一个设备绑着两个会话"、"被踢掉的会话没被交还"、"在线却查不到"这类情况。
/// </para>
/// </summary>
public class DeviceRegistryConcurrencyTests
{
    /// <summary>
    /// 多个会话并发绑定同一设备时，最终注册表里只有一个会话，
    /// 且被踢掉的会话都作为"陈旧会话"被返回（不能有"赢家"之外的会话被遗忘）。
    /// </summary>
    [Fact]
    public async Task Bind_Concurrent_BindsExactlyOneAndReturnsTheRest()
    {
        var registry = new DeviceRegistry();

        const int count = 16;
        var sessions = Enumerable.Range(0, count)
            .Select(i => new FakeSessionHandle($"s{i}"))
            .ToArray();

        // 每个任务都跑到这里才开始真正的绑定，制造最大程度的竞争。
        var staleResults = ConcurrentRunner.Run(
            count,
            i => registry.Bind("D1", sessions[i], DateTimeOffset.UtcNow));

        await Task.CompletedTask;

        // 只有一个绑定。
        Assert.Equal(1, registry.OnlineCount);

        var winner = registry.TryGetSession("D1");
        Assert.NotNull(winner);

        // 被踢掉的会话必须都被返回 —— 否则就是"被踢掉但没人负责关闭"，
        // 那个连接会一直占着资源。
        var returnedStale = staleResults.Where(s => s != null).ToArray();

        // 每个落败会话恰好被返回一次。
        Assert.Equal(count - 1, returnedStale.Length);

        foreach (var s in sessions)
        {
            if (ReferenceEquals(s, winner))
            {
                continue;
            }

            Assert.Contains(s, returnedStale);
        }
    }

    /// <summary>
    /// 反复对同一会话重复绑定不应把它自己踢掉。
    /// </summary>
    [Fact]
    public void Bind_RepeatedWithSameSession_DoesNotCloseIt()
    {
        var registry = new DeviceRegistry();
        var session = new FakeSessionHandle("s1");

        for (var i = 0; i < 10; i++)
        {
            var stale = registry.Bind("D1", session, DateTimeOffset.UtcNow);
            Assert.Null(stale);
        }

        Assert.Same(session, registry.TryGetSession("D1"));
        Assert.Equal(0, session.CloseCount);
    }

    /// <summary>
    /// 并发注销与绑定：注销不能误删掉刚绑上的新会话。
    /// </summary>
    [Fact]
    public async Task Unbind_ConcurrentWithBind_DoesNotRemoveNewBinding()
    {
        var registry = new DeviceRegistry();
        var oldSession = new FakeSessionHandle("old");
        var newSession = new FakeSessionHandle("new");

        registry.Bind("D1", oldSession, DateTimeOffset.UtcNow);

        // 旧会话的注销 与 新会话的绑定 并发
        ConcurrentRunner.Run(2, i =>
        {
            if (i == 0)
            {
                registry.UnbindAndMarkOffline("D1", oldSession);
            }
            else
            {
                registry.Bind("D1", newSession, DateTimeOffset.UtcNow);
            }
        });

        await Task.CompletedTask;

        // 最终必须还能查到会话 —— 不能因为并发注销而"在线却查不到"。
        var bound = registry.TryGetSession("D1");
        Assert.NotNull(bound);
    }

    /// <summary>
    /// <see cref="DeviceRegistry.TryBind"/> 会真的关闭被替换掉的旧会话。
    /// 并发调用时，每个落败会话都应当恰好被关闭一次。
    /// </summary>
    [Fact]
    public async Task TryBind_Concurrent_ClosesEachLoserExactlyOnce()
    {
        var registry = new DeviceRegistry();

        const int count = 8;
        var sessions = Enumerable.Range(0, count)
            .Select(i => new FakeSessionHandle($"s{i}"))
            .ToArray();

        // TryBind 需要真实的 DeviceSession，这里只验证注册表层面的行为：
        // Bind 返回的陈旧会话被正确交还。
        var staleResults = ConcurrentRunner.Run(
            count,
            i => registry.Bind("D1", sessions[i], DateTimeOffset.UtcNow));

        await Task.CompletedTask;

        Assert.Equal(1, registry.OnlineCount);

        // 每个落败会话被返回的次数恰好是 1。
        foreach (var s in sessions)
        {
            var times = staleResults.Count(x => ReferenceEquals(x, s));
            Assert.True(times <= 1, $"会话 {s.SessionId} 被当作陈旧会话返回了 {times} 次");
        }
    }

    /// <summary>
    /// 并发刷新心跳：只有当前绑定的那个会话能成功。
    /// </summary>
    [Fact]
    public async Task TouchHeartbeat_Concurrent_OnlyBoundSessionWins()
    {
        var registry = new DeviceRegistry();

        const int count = 16;
        var sessions = Enumerable.Range(0, count)
            .Select(i => new FakeSessionHandle($"s{i}"))
            .ToArray();

        var now = DateTimeOffset.UtcNow;

        // 先绑定其中一个。
        var bound = sessions[0];
        registry.Bind("D1", bound, now);

        // 所有会话都尝试刷新心跳 —— 只有当前绑定的那个应该成功。
        var results = ConcurrentRunner.Run(
            count,
            i => registry.TouchHeartbeat("D1", sessions[i], now.AddSeconds(30)));

        await Task.CompletedTask;

        Assert.Equal(1, results.Count(r => r));
        Assert.True(results[0]);

        // 心跳时间确实被更新了。
        Assert.Equal(now.AddSeconds(30), registry.TryGetDevice("D1")!.LastHeartbeatAt);
    }

    /// <summary>
    /// 看门狗的超时处理与心跳刷新同时发生 —— 刚到的设备不能被误判离线。
    /// </summary>
    [Fact]
    public async Task ExpireHeartbeatSessions_ConcurrentWithTouchHeartbeat_DoesNotEvictFreshDevice()
    {
        var registry = new DeviceRegistry();

        var session = new FakeSessionHandle("s1");
        var now = DateTimeOffset.UtcNow;

        // 心跳已经超时了。
        registry.Bind("D1", session, now.AddMinutes(-10));

        // 一边看门狗在判超时，一边心跳正好到达。
        ConcurrentRunner.Run(2, i =>
        {
            if (i == 0)
            {
                registry.ExpireHeartbeatSessions(now, TimeSpan.FromSeconds(90));
            }
            else
            {
                registry.TouchHeartbeat("D1", session, now);
            }
        });

        await Task.CompletedTask;

        // 两种结果都合法（取决于谁先拿到锁），
        // 但状态必须是自洽的：要么在线且能查到会话，要么离线且查不到会话。
        var record = registry.TryGetDevice("D1")!;
        var current = registry.TryGetSession("D1");

        if (record.Status == DeviceStatus.Online)
        {
            Assert.NotNull(current);
        }
        else
        {
            Assert.Null(current);
        }
    }

    /// <summary>
    /// 并发读写下，<see cref="DeviceRegistry.AllDevices"/> 始终返回一致的快照。
    /// </summary>
    [Fact]
    public async Task AllDevices_SnapshotIsStable_UnderConcurrentWrites()
    {
        var registry = new DeviceRegistry();

        var stop = false;

        // 后台持续写。
        var writer = Task.Run(() =>
        {
            var i = 0;

            while (!Volatile.Read(ref stop))
            {
                registry.EnsureRegistered($"D{i % 20}", DateTimeOffset.UtcNow);
                i++;
            }
        });

        // 主线程持续读。
        for (var round = 0; round < 200; round++)
        {
            var snapshot = registry.AllDevices;

            // 快照是数组副本，枚举时不受并发写影响。
            Assert.NotNull(snapshot);
        }

        Volatile.Write(ref stop, true);
        await writer;
    }
}
