using System.Buffers;
using System.Text.Json;
using DeviceGateway.Shared.Protocol;

namespace DeviceGateway.Tests;

/// <summary>
/// 编码器测试：编码出来的帧必须符合 0x55 | LEN(4) | TYPE(1) | JSON | 0xAA 的格式，
/// 并且能被解码器还原。
/// </summary>
public class DeviceFrameEncoderTests
{
    /// <summary>
    /// 编码结果的长度、帧头帧尾位置必须精确。
    /// </summary>
    [Fact]
    public void Encode_WritesExpectedFrameLayout()
    {
        var encoder = new DeviceFrameEncoder();
        var buffer = new ArrayBufferWriter<byte>();

        var payload = """{"deviceId":"DRONE-001"}""";
        var written = encoder.Encode(buffer, new DevicePackage
        {
            Key = DeviceMessageTypes.Heartbeat,
            Payload = payload
        });

        var bytes = buffer.WrittenSpan.ToArray();

        Assert.Equal(bytes.Length, written);

        // 帧头
        Assert.Equal(DeviceFrame.BeginMark, bytes[0]);

        // 长度域 = JSON 负载字节数（不含类型字节、不含长度域本身）
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4));
        var payloadByteCount = System.Text.Encoding.UTF8.GetByteCount(payload);
        Assert.Equal((uint)payloadByteCount, length);

        // 消息类型
        Assert.Equal(DeviceMessageTypes.Heartbeat, bytes[5]);

        // 帧尾
        Assert.Equal(DeviceFrame.EndMark, bytes[DeviceFrame.EndMarkOffset(payloadByteCount)]);

        // 整帧长度
        Assert.Equal(DeviceFrame.TotalLength(payloadByteCount), bytes.Length);
    }

    /// <summary>
    /// 编码 → 解码往返，字段必须一致。
    /// </summary>
    [Theory]
    [InlineData(DeviceMessageTypes.Heartbeat)]
    [InlineData(DeviceMessageTypes.Position)]
    public void EncodeThenDecode_RoundTrips(byte messageType)
    {
        var encoder = new DeviceFrameEncoder();
        var buffer = new ArrayBufferWriter<byte>();
        const string payload = """{"deviceId":"DRONE-002","lat":30.123456,"lng":120.654321,"alt":88.5}""";

        encoder.Encode(buffer, new DevicePackage { Key = messageType, Payload = payload });

        var filter = new DeviceFrameFilter();
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(buffer.WrittenMemory));

        var decoded = filter.Filter(ref reader);

        Assert.NotNull(decoded);
        Assert.Equal(messageType, decoded!.Key);
        Assert.Equal(payload, decoded.Payload);
    }

    /// <summary>
    /// 中文负载不能被转义成 \uXXXX —— 沿用仓库既有的序列化约定。
    /// </summary>
    [Fact]
    public void Encode_DoesNotEscapeChineseCharacters()
    {
        var encoder = new DeviceFrameEncoder();
        var buffer = new ArrayBufferWriter<byte>();
        const string payload = """{"deviceId":"无人机-001"}""";

        encoder.Encode(buffer, new DevicePackage
        {
            Key = DeviceMessageTypes.Position,
            Payload = payload
        });

        var text = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);

        Assert.Contains("无人机-001", text);
        Assert.DoesNotContain("\\u", text);
    }

    /// <summary>
    /// 负载是合法 JSON 时，编码出去的内容必须能被反序列化回原对象。
    /// </summary>
    [Fact]
    public void Encode_ProducesValidJsonPayload()
    {
        var encoder = new DeviceFrameEncoder();
        var buffer = new ArrayBufferWriter<byte>();

        encoder.Encode(buffer, new DevicePackage
        {
            Key = DeviceMessageTypes.Position,
            Payload = """{"deviceId":"D1","lat":1.5}"""
        });

        var filter = new DeviceFrameFilter();
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var decoded = filter.Filter(ref reader);

        using var doc = JsonDocument.Parse(decoded!.Payload);
        Assert.Equal("D1", doc.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal(1.5, doc.RootElement.GetProperty("lat").GetDouble());
    }
}
