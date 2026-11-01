namespace DeviceGateway.Server.Controllers;

/// <summary>
/// 跨设备消息的请求体。
/// </summary>
public class SendMessageRequest
{
    /// <summary>
    /// 发送方设备标识。
    /// </summary>
    public string From { get; set; } = string.Empty;

    /// <summary>
    /// 接收方设备标识。
    /// </summary>
    public string To { get; set; } = string.Empty;

    /// <summary>
    /// 消息内容（任意 JSON，会原样放进数据帧的负载里）。
    /// </summary>
    public object? Payload { get; set; }
}

/// <summary>
/// 设备列表项。
/// </summary>
public class DeviceInfo
{
    /// <summary>
    /// 设备标识。
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// 厂家标识。
    /// </summary>
    public string Vendor { get; set; } = string.Empty;

    /// <summary>
    /// 在线状态。
    /// </summary>
    public string Status { get; set; } = "Offline";

    /// <summary>
    /// 最后心跳时间（UTC）。
    /// </summary>
    public DateTimeOffset LastHeartbeatAt { get; set; }

    /// <summary>
    /// 首次出现时间（UTC）。
    /// </summary>
    public DateTimeOffset FirstSeenAt { get; set; }
}

/// <summary>
/// 设备不存在时的响应。
/// </summary>
public class DeviceNotFoundResponse
{
    /// <summary>
    /// 设备标识。
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// 状态标识。unknown 表示设备从没上报过。
    /// </summary>
    public string Status { get; set; } = "unknown";
}

/// <summary>
/// 设备当前不在线时的响应。
/// </summary>
public class DeviceOfflineResponse
{
    /// <summary>
    /// 设备标识。
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// 状态标识。offline 表示设备存在但当前没有在线连接。
    /// </summary>
    public string Status { get; set; } = "offline";
}

/// <summary>
/// 消息发送成功的响应。
/// </summary>
public class SendMessageResponse
{
    /// <summary>
    /// 状态标识。sent 表示已入队发往目标设备。
    /// </summary>
    public string Status { get; set; } = "sent";

    /// <summary>
    /// 发送方设备标识。
    /// </summary>
    public string From { get; set; } = string.Empty;

    /// <summary>
    /// 接收方设备标识。
    /// </summary>
    public string To { get; set; } = string.Empty;
}
