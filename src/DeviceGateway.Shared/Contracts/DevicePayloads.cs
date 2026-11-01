namespace DeviceGateway.Shared.Contracts;

/// <summary>
/// 心跳消息的负载。设备每 30 秒发一次。
/// </summary>
public class HeartbeatPayload
{
    /// <summary>
    /// 设备唯一标识。服务端据此绑定会话并登记设备。
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// 设备时间戳（毫秒）。仅作参考，离线判定用的是服务端自己记录的心跳时间。
    /// </summary>
    public long Timestamp { get; set; }
}

/// <summary>
/// 无人机位置数据。设备每 1 秒发一次。
/// </summary>
public class PositionPayload
{
    /// <summary>
    /// 设备唯一标识。
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// 纬度。
    /// </summary>
    public double Latitude { get; set; }

    /// <summary>
    /// 经度。
    /// </summary>
    public double Longitude { get; set; }

    /// <summary>
    /// 海拔/高度（米）。
    /// </summary>
    public double Altitude { get; set; }

    /// <summary>
    /// 速度（米/秒）。
    /// </summary>
    public double Speed { get; set; }

    /// <summary>
    /// 航向角（度，0~360）。
    /// </summary>
    public double Heading { get; set; }

    /// <summary>
    /// 设备时间戳（毫秒）。
    /// </summary>
    public long Timestamp { get; set; }
}
