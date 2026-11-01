namespace DeviceGateway.Server.Registry;

/// <summary>
/// 设备在线状态。
/// </summary>
public enum DeviceStatus
{
    /// <summary>离线：没有活跃连接，或心跳已超时。</summary>
    Offline = 0,

    /// <summary>在线：有活跃连接且心跳未超时。</summary>
    Online = 1
}

/// <summary>
/// 一台设备的档案。
/// <para>
/// 设备档案与"在线会话"是分开的两件事：设备断线后档案依然存在（可以查得到它是谁），
/// 只是状态变成离线。这个区分是"首次上报自动添加设备"和"离线判定"能同时正确的前提。
/// </para>
/// </summary>
public record DeviceRecord
{
    /// <summary>
    /// 设备唯一标识。
    /// </summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>
    /// 设备厂家标识。后续接入多厂家时用来区分来源。
    /// </summary>
    public string Vendor { get; init; } = "vendorA";

    /// <summary>
    /// 首次上报的时间（UTC）。
    /// </summary>
    public DateTimeOffset FirstSeenAt { get; init; }

    /// <summary>
    /// 最后一次收到心跳的时间（UTC）。离线判定只看这个时间。
    /// </summary>
    public DateTimeOffset LastHeartbeatAt { get; set; }

    /// <summary>
    /// 最近一次连接时间（UTC）
    /// </summary>
    public DateTimeOffset ConnectedAt { get; init; }

    /// <summary>
    /// 当前在线状态。
    /// </summary>
    public DeviceStatus Status { get; set; } = DeviceStatus.Offline;
}
