namespace DeviceGateway.Shared.Protocol;

/// <summary>
/// 设备消息类型（帧中 1 字节的类型字段）。
/// <para>
/// 服务端按这个字节路由到对应的命令处理器。新增厂家时在自己的程序集里定义同构的类型常量，
/// 不要改动这里已有厂家的取值。
/// </para>
/// </summary>
public static class DeviceMessageTypes
{
    /// <summary>心跳。设备每 30 秒发送一次，用于在线状态判定。</summary>
    public const byte Heartbeat = 0x01;

    /// <summary>无人机位置数据。设备每秒发送一次。</summary>
    public const byte Position = 0x02;

    /// <summary>通用数据上报（预留）。</summary>
    public const byte Report = 0x03;
}
