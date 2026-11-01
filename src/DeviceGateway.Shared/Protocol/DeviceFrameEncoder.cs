using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SuperSocket.ProtoBase;

namespace DeviceGateway.Shared.Protocol;

/// <summary>
/// 把 <see cref="DevicePackage"/> 编码成 <c>0x55 | LEN(4) | TYPE(1) | JSON | 0xAA</c> 帧。
/// <para>
/// 帧格式与长度域的大端序约定与仓库中既有的 <c>DefaultEncoder</c> / <c>MyPackageEncoder</c> 保持一致，
/// 只是增加了 1 字节消息类型。
/// </para>
/// </summary>
public sealed class DeviceFrameEncoder : IPackageEncoder<DevicePackage>
{
    /// <summary>
    /// 编码一个消息包到缓冲区。
    /// </summary>
    /// <param name="writer">目标缓冲区。</param>
    /// <param name="package">待编码的消息包。</param>
    /// <returns>写入的字节数。</returns>
    public int Encode(IBufferWriter<byte> writer, DevicePackage package)
    {
        // Payload 本身已经是 JSON 文本，直接按 UTF-8 写入帧里，
        // 不做二次序列化（否则会被当成 JSON 字符串再包一层引号）。
        var payloadLength = Encoding.UTF8.GetByteCount(package.Payload);

        // 预留整个帧的空间，一次性写入，避免多次 GetSpan/Advance 造成缓冲区碎片。
        var span = writer.GetSpan(DeviceFrame.TotalLength(payloadLength));

        var offset = 0;

        span[offset] = DeviceFrame.BeginMark;
        offset += 1;

        // 长度域大端序，与解码端保持一致。
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offset, DeviceFrame.LengthFieldSize), (uint)payloadLength);
        offset += DeviceFrame.LengthFieldSize;

        span[offset] = package.MessageType;
        offset += DeviceFrame.MessageTypeFieldSize;

        offset += Encoding.UTF8.GetBytes(package.Payload, span.Slice(offset, payloadLength));

        span[offset] = DeviceFrame.EndMark;
        offset += 1;

        writer.Advance(offset);
        return offset;
    }
}
