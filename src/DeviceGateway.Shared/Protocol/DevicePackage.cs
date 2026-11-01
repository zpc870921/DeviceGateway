using SuperSocket.ProtoBase;

namespace DeviceGateway.Shared.Protocol;

/// <summary>
/// 一个设备消息包。
/// <para>
/// 实现 <see cref="IKeyedPackageInfo{byte}"/> 是为了让 SuperSocket 的命令模式按
/// <see cref="Key"/>（即帧里的 1 字节消息类型）路由到对应的命令处理器。
/// </para>
/// </summary>
public sealed class DevicePackage : IKeyedPackageInfo<byte>
{
    /// <summary>
    /// 消息类型字节。SuperSocket 的命令路由就是按这个值查找命令的。
    /// </summary>
    public byte Key { get; set; }

    /// <summary>
    /// 消息类型字节（与 <see cref="Key"/> 同值），命名上更贴近业务语义的别名。
    /// </summary>
    public byte MessageType => Key;

    /// <summary>
    /// JSON 负载原文（UTF-8 解码后的字符串）。
    /// </summary>
    public string Payload { get; set; } = string.Empty;
}
