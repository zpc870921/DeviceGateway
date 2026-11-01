using DeviceGateway.Server.Sessions;
using System.Collections.Concurrent;

namespace DeviceGateway.Server.Registry;

/// <summary>
/// 设备注册表。
///
/// 负责：
/// 1. 设备档案管理
/// 2. 设备与 Session 绑定
/// 3. 设备重连管理
/// 4. 设备上下线管理
/// 5. 设备心跳管理
/// 6. 心跳超时管理
///
/// 线程安全：
/// 所有设备档案和 Session 的读写操作统一使用 _gate 保护，
/// 保证设备状态转换的一致性。
/// </summary>
public sealed class DeviceRegistry
{
    /// <summary>
    /// 统一同步锁。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// 设备档案表。
    ///
    /// DeviceId -> DeviceRecord
    ///
    /// 设备断开连接后，档案依然保留。
    /// </summary>
    private readonly Dictionary<string, DeviceRecord> _devices =new(StringComparer.Ordinal);

    /// <summary>
    /// 在线会话表。
    ///
    /// DeviceId -> 当前在线 Session
    ///
    /// 设备断开连接后，从这里移除。
    /// </summary>
    private readonly Dictionary<string, ISessionHandle> _sessions =new(StringComparer.Ordinal);

    #region 查询

    /// <summary>
    /// 获取所有设备档案快照。
    /// </summary>
    public IReadOnlyCollection<DeviceRecord> AllDevices
    {
        get
        {
            lock (_gate)
            {
                return _devices.Values.ToArray();
            }
        }
    }

    /// <summary>
    /// 当前在线设备数量。
    /// </summary>
    public int OnlineCount
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>
    /// 根据 DeviceId 获取当前在线 Session。
    /// </summary>
    public ISessionHandle? TryGetSession(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        lock (_gate)
        {
            return _sessions.TryGetValue(deviceId, out var session)
                ? session
                : null;
        }
    }

    /// <summary>
    /// 根据 DeviceId 获取设备档案。
    /// </summary>
    public DeviceRecord? TryGetDevice(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        lock (_gate)
        {
            return _devices.TryGetValue(deviceId, out var record)
                ? record
                : null;
        }
    }

    #endregion

    #region 设备登记

    /// <summary>
    /// 确保设备档案存在。
    ///
    /// 注意：
    /// 这里只负责登记，不负责将设备设置为在线。
    /// 设备是否在线由 Bind 决定。
    /// </summary>
    public DeviceRecord EnsureRegistered(string deviceId,DateTimeOffset now)
    {
        ValidateDeviceId(deviceId);

        lock (_gate)
        {
            if (_devices.TryGetValue(deviceId, out var existing))
            {
                return existing;
            }

            var record = new DeviceRecord
            {
                DeviceId = deviceId,
                FirstSeenAt = now,
                LastHeartbeatAt = now,
                ConnectedAt = default,
                Status = DeviceStatus.Offline
            };

            _devices.Add(deviceId, record);

            return record;
        }
    }

    #endregion

    #region 绑定设备

    /// <summary>
    /// 绑定设备与 Session。
    ///
    /// 如果设备已经存在其他连接：
    /// 1. 使用新 Session 替换旧 Session。
    /// 2. 返回被替换的旧 Session。
    /// 3. 由调用者在锁外关闭旧 Session。
    ///
    /// 返回 null 表示没有旧 Session 被替换。
    ///
    /// <para>
    /// <b>这里不改变设备的在线状态，也不刷新心跳时间。</b>
    /// 绑定只回答"这条连接属于哪台设备"，而"设备是否在线"由心跳决定 ——
    /// 见 <see cref="TouchHeartbeat"/>。
    /// </para>
    /// <para>
    /// 原因：看门狗判离线之后，设备 TCP 连接可能还在（它还在发位置数据）。
    /// 如果这里把状态改回 Online，心跳停了的设备就永远判不出离线。
    /// </para>
    /// </summary>
    public ISessionHandle? Bind(string deviceId,ISessionHandle session,DateTimeOffset now)
    {
        ValidateDeviceId(deviceId);
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            // 1. 确保设备档案存在
            if (!_devices.TryGetValue(deviceId, out var record))
            {
                record = new DeviceRecord
                {
                    DeviceId = deviceId,
                    FirstSeenAt = now,
                    LastHeartbeatAt = now,
                    ConnectedAt = now,
                    Status = DeviceStatus.Offline
                };

                _devices.Add(deviceId, record);
            }

            // 2. 查找旧 Session
            ISessionHandle? staleSession = null;

            if (_sessions.TryGetValue(deviceId, out var existing))
            {
                // 同一个 Session 重复绑定
                if (ReferenceEquals(existing, session))
                {
                    return null;
                }

                staleSession = existing;
            }

            // 3. 绑定新 Session
            _sessions[deviceId] = session;

            // 4. 记录连接时间。
            //    在线状态与心跳时间不动 —— 那两件事只由心跳决定。
            _devices[deviceId] = record with
            {
                ConnectedAt = now
            };

            // 5. 返回被替换的旧 Session
            return staleSession;
        }
    }

    #endregion

    #region 解绑设备

    /// <summary>
    /// 解绑设备并标记离线。
    ///
    /// 只有当前绑定的 Session 才能执行成功。
    ///
    /// 如果设备已经重连，旧 Session 的关闭事件不会影响新 Session。
    /// </summary>
    public bool UnbindAndMarkOffline(string deviceId,ISessionHandle session)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            // 1. 查询当前绑定的 Session
            if (!_sessions.TryGetValue(deviceId, out var current))
            {
                return false;
            }

            // 2. 判断是不是当前 Session
            if (!ReferenceEquals(current, session))
            {
                return false;
            }

            // 3. 删除在线 Session
            _sessions.Remove(deviceId);

            // 4. 更新设备状态
            if (_devices.TryGetValue(deviceId, out var record))
            {
                _devices[deviceId] = record with
                {
                    Status = DeviceStatus.Offline
                };
            }

            return true;
        }
    }

    #endregion

    #region 心跳管理

    /// <summary>
    /// 校验给定的会话是否仍是该设备当前的在线会话。
    /// <para>
    /// 位置数据用它来识别"旧连接"，但它<b>不</b>刷新心跳时间、
    /// <b>不</b>改变在线状态 —— 离线判定只认心跳。
    /// </para>
    /// </summary>
    /// <param name="deviceId">设备标识。</param>
    /// <param name="session">待校验的会话。</param>
    /// <returns>该会话是当前绑定时返回 true。</returns>
    public bool IsCurrentSession(string deviceId, ISessionHandle session)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            return _sessions.TryGetValue(deviceId, out var current)
                && ReferenceEquals(current, session);
        }
    }

    /// <summary>
    /// 刷新设备心跳。
    ///
    /// 只有当前绑定的 Session 才允许刷新。
    /// 防止旧连接的数据处理任务影响新连接。
    /// </summary>
    public bool TouchHeartbeat(string deviceId,ISessionHandle session,DateTimeOffset now)
    {
        ValidateDeviceId(deviceId);
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            // 1. 必须存在当前在线 Session，且必须是当前绑定的那个
            if (!_sessions.TryGetValue(deviceId, out var current) ||
                !ReferenceEquals(current, session))
            {
                return false;
            }

            // 2. 必须存在设备档案
            if (!_devices.TryGetValue(deviceId, out var record))
            {
                return false;
            }

            // 3. 防止心跳时间倒退
            var lastHeartbeatAt = now > record.LastHeartbeatAt
                ? now
                : record.LastHeartbeatAt;

            // 4. 更新心跳时间和在线状态
            _devices[deviceId] = record with
            {
                LastHeartbeatAt = lastHeartbeatAt,
                Status = DeviceStatus.Online
            };

            return true;
        }
    }

    #endregion

    #region 心跳超时

    /// <summary>
    /// 找出心跳超时的设备档案快照。
    ///
    /// 只查询，不修改设备状态。
    /// 如果需要真正将设备标记为离线，
    /// 应使用 ExpireHeartbeatSessions。
    /// </summary>
    public List<DeviceRecord> FindHeartbeatExpired(DateTimeOffset now,TimeSpan timeout)
    {
        ValidateTimeout(timeout);

        lock (_gate)
        {
            return _devices.Values
                .Where(record =>
                    record.Status == DeviceStatus.Online &&
                    now - record.LastHeartbeatAt > timeout)
                .ToList();
        }
    }

    /// <summary>
    /// 处理心跳超时设备。
    ///
    /// 在同一个锁中完成：
    /// 1. 检测心跳是否超时
    /// 2. 移除在线 Session
    /// 3. 标记设备离线
    ///
    /// 返回需要关闭的 Session。
    ///
    /// 注意：这里只负责 Registry 状态转换，
    /// 不在锁内关闭 TCP Session。
    /// </summary>
    public List<ISessionHandle> ExpireHeartbeatSessions(DateTimeOffset now,TimeSpan timeout)
    {
        // 第一步：获取设备快照
        DeviceRecord[] snapshot;

        lock (_gate)
        {
            snapshot = _devices.Values.ToArray();
        }

        // 第二步：锁外筛选候选设备
        var candidates = snapshot
            .Where(x =>
                x.Status == DeviceStatus.Online &&
                now - x.LastHeartbeatAt > timeout)
            .ToArray();

        var expiredSessions = new List<ISessionHandle>();

        // 第三步：逐个设备进行最终确认
        foreach (var candidate in candidates)
        {
            lock (_gate)
            {
                // 重新读取最新设备状态
                if (!_devices.TryGetValue(
                        candidate.DeviceId,
                        out var current))
                {
                    continue;
                }

                // 状态可能已经发生变化
                if (current.Status != DeviceStatus.Online)
                    continue;

                // 重新检查心跳时间
                if (now - current.LastHeartbeatAt <= timeout)
                    continue;

                // 获取当前 Session
                if (_sessions.TryGetValue(
                        candidate.DeviceId,
                        out var session))
                {
                    _sessions.Remove(candidate.DeviceId);

                    expiredSessions.Add(session);
                }

                // 标记离线
                _devices[candidate.DeviceId] = current with
                {
                    Status = DeviceStatus.Offline
                };
            }
        }

        return expiredSessions;
    }

    #endregion

    #region 设备绑定入口

    /// <summary>
    /// 把会话与设备绑定起来 —— 设备消息处理的统一入口。
    /// <para>
    /// 做了三件事：
    /// <list type="number">
    ///   <item>校验设备标识：首次调用绑定 deviceId，之后每帧只做一致性校验，
    ///   防止任何客户端伪造别人的 deviceId 来劫持会话绑定。</item>
    ///   <item>设备不存在就自动添加（首次上报的场景）。</item>
    ///   <item>把会话登记到注册表 —— 跨设备消息靠这个找到目标设备的连接。</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="session">设备会话。</param>
    /// <param name="deviceId">设备标识。</param>
    /// <param name="now">当前时间。</param>
    /// <returns>绑定成功返回 true；设备标识与已绑定的不一致时返回 false。</returns>
    public async Task<bool> TryBind(DeviceSession session,string deviceId, DateTimeOffset now)
    {
        // 1. 校验设备标识。
        //    首次调用会把 deviceId 绑定到会话上；之后每帧只做一致性校验，
        //    防止任何客户端伪造别人的 deviceId 来劫持会话绑定。
        if (!session.TryBindDevice(deviceId))
        {
            session.LogWarning(
                "会话 {SessionId} 声称的设备标识 {Claimed} 与已绑定的 {Bound} 不一致，已丢弃",
                session.SessionID, deviceId, session.DeviceId);

            return false;
        }

        // 2. 把会话登记到注册表 —— 跨设备消息靠这个找到目标设备的连接。
        //    Bind 内部会在档案缺失时自动建档（设备首次上报的场景），
        //    同一设备重连时则会返回被替换的旧会话。
        var staleSession = Bind(deviceId, session, now);

        // 3. 旧连接在锁外关闭 —— 关闭可能要等待网络操作，不能占着注册表的锁。
        if (staleSession != null)
        {
            try
            {
                await staleSession.CloseAsync();
            }
            catch (Exception ex)
            {
                session.LogError(
                    "关闭设备旧连接失败，DeviceId: {DeviceId}，异常={Exception}",
                    deviceId,
                    ex.Message);
            }
        }

        return true;
    }

    #endregion

    #region 辅助方法

    private static void ValidateDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException(
                "DeviceId 不能为空。",
                nameof(deviceId));
        }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "心跳超时时间不能小于零。");
        }
    }

    #endregion
}
