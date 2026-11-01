namespace DeviceGateway.Shared.Protocol;

/// <summary>
/// 设备协议的帧常量与长度计算。
/// <para>
/// 帧格式：<c>0x55 | LEN(4字节大端) | TYPE(1字节) | JSON(n字节) | 0xAA</c>，
/// 其中 LEN 仅为 JSON 负载的字节数 n，不含类型字节、长度域本身和帧尾。
/// </para>
/// <para>
/// 这是服务端与客户端共用的唯一长度定义来源，任何偏移量改动只改这里。
/// </para>
/// </summary>
public static class DeviceFrame
{
    /// <summary>帧起始标记。</summary>
    public const byte BeginMark = 0x55;

    /// <summary>帧结束标记。</summary>
    public const byte EndMark = 0xAA;

    /// <summary>长度字段占用的字节数（4 字节大端序）。</summary>
    public const int LengthFieldSize = 4;

    /// <summary>消息类型字段占用的字节数（1 字节）。</summary>
    public const int MessageTypeFieldSize = 1;

    /// <summary>
    /// 帧头长度：起始标记(1) + 长度域(4) + 消息类型(1)。
    /// </summary>
    public const int HeaderSize = 1 + LengthFieldSize + MessageTypeFieldSize;

    /// <summary>
    /// 计算整个帧的字节数（含帧头与帧尾）。
    /// </summary>
    /// <param name="jsonPayloadLength">JSON 负载字节数 n。</param>
    /// <returns>整帧长度 = n + 6。</returns>
    public static int TotalLength(int jsonPayloadLength) => jsonPayloadLength + HeaderSize + 1;

    /// <summary>
    /// 计算帧尾在整帧中的偏移（即帧尾字节的下标）。
    /// </summary>
    /// <param name="jsonPayloadLength">JSON 负载字节数 n。</param>
    /// <returns>帧尾字节下标 = n + 5。</returns>
    public static int EndMarkOffset(int jsonPayloadLength) => HeaderSize + jsonPayloadLength;
}
