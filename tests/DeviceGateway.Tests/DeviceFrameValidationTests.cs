using System.Buffers;
using System.Text;
using DeviceGateway.Shared.Protocol;
using DeviceGateway.Tests.Support;
using SuperSocket.ProtoBase;

namespace DeviceGateway.Tests;

/// <summary>
/// 帧头 / 帧尾 / 长度 三项验证的专项测试。
/// <para>
/// 这三项是协议的第一道防线：任何一项没拦住，脏数据就会被当成正常数据投递给业务，
/// 或者导致流永久卡死。
/// </para>
/// <para>
/// 共同约束：三项验证失败时都<b>不能抛异常</b> —— 抛异常会让 SuperSocket
/// 以 <c>CloseReason.ProtocolError</c> 关闭连接，那是断连而不是容错。
/// </para>
/// </summary>
public class DeviceFrameValidationTests
{
    // ------------------------------------------------------------------
    // 帧头验证
    // ------------------------------------------------------------------

    /// <summary>
    /// 帧头必须是 0x55。前面有脏字节时要能跳过去找到真正的帧。
    /// </summary>
    [Fact]
    public void Validate_FrameHeader_MustBeBeginMark()
    {
        var filter = new DeviceFrameFilter();

        // 破坏帧头：把 0x55 换成别的字节
        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Heartbeat,
            """{"deviceId":"D1"}""",
            overrideBeginMark: 0x56);

        Assert.Empty(FrameDecoder.Decode(filter, frame));
    }

    /// <summary>
    /// 帧头之前的脏字节要被跳过，且不能影响后面真帧的解析。
    /// </summary>
    [Fact]
    public void Validate_FrameHeader_SkipsGarbageBeforeFrame()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"D1"}""");

        var garbage = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var buffer = garbage.Concat(frame).ToArray();

        var packages = FrameDecoder.Decode(filter, buffer);

        var package = Assert.Single(packages);
        Assert.Equal("""{"deviceId":"D1"}""", package.Payload);
    }

    /// <summary>
    /// 帧头差一个字节（只有 0x55 + 长度域，缺类型字节）时，
    /// 必须判定为"数据不足"而不是越界读。
    /// </summary>
    /// <remarks>
    /// 这是一条回归测试：帧头一共 6 字节（0x55 + LEN(4) + TYPE(1)）。
    /// 如果完整性检查写成了"5 字节"，那么恰好收到 5 字节时就会越界读，
    /// 静默得到一个类型为 0 的假包。
    /// </remarks>
    [Fact]
    public void Validate_HeaderIncomplete_DoesNotReadOutOfBounds()
    {
        var filter = new DeviceFrameFilter();

        // 0x55 + LEN(4)，缺消息类型字节。
        var partial = new byte[] { DeviceFrame.BeginMark, 0x00, 0x00, 0x00, 0x02 };

        var call = FrameDecoder.Call(filter, partial);

        Assert.Null(call.Package);

        // 数据不足 ⇒ 必须消费 0 字节，把已收到的部分留在缓冲区里等下一批。
        Assert.Equal(0, call.Consumed);
    }

    // ------------------------------------------------------------------
    // 长度验证
    // ------------------------------------------------------------------

    /// <summary>
    /// 长度域超过上限必须被拒绝 —— 防止伪造长度导致巨额内存分配。
    /// </summary>
    [Fact]
    public void Validate_Length_RejectsOversizedPayload()
    {
        var filter = new DeviceFrameFilter();

        // 长度域声明 10 MB，但实际只带几个字节
        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Heartbeat,
            """{"deviceId":"D1"}""",
            overrideLength: 10 * 1024 * 1024);

        Assert.Empty(FrameDecoder.Decode(filter, frame));
    }

    /// <summary>
    /// 长度域恰好等于上限（64 KB）时必须接受 —— 上限是闭区间。
    /// </summary>
    [Fact]
    public void Validate_Length_AcceptsPayloadExactlyAtLimit()
    {
        var filter = new DeviceFrameFilter();

        // 造一个正好 64 KB 的负载。
        var payload = new byte[DeviceFrameFilter.MaxPayloadLength];
        Encoding.UTF8.GetBytes("""{"deviceId":"D1"}""").CopyTo(payload, 0);

        var frame = FrameDecoder.BuildFrame(DeviceMessageTypes.Report, payload);

        var packages = FrameDecoder.Decode(filter, frame);

        Assert.Single(packages);
    }

    /// <summary>
    /// 长度域为 0（空负载）是合法的 —— 最小的合法帧是 7 字节。
    /// </summary>
    /// <remarks>
    /// 协议层只做<b>结构</b>校验，不掺和业务语义。
    /// 空负载在业务层会被 <c>DevicePayloadReader.TryReadDeviceId</c> 丢弃，
    /// 但那是另一层的事。
    /// </remarks>
    [Fact]
    public void Validate_Length_AcceptsZeroLengthPayload()
    {
        var filter = new DeviceFrameFilter();

        // 手工构造：0x55 | len=0 | type | 0xAA
        var frame = new byte[]
        {
            DeviceFrame.BeginMark, 0x00, 0x00, 0x00, 0x00,
            DeviceMessageTypes.Heartbeat, DeviceFrame.EndMark
        };

        var packages = FrameDecoder.Decode(filter, frame);

        var package = Assert.Single(packages);
        Assert.Equal(DeviceMessageTypes.Heartbeat, package.Key);
        Assert.Equal(string.Empty, package.Payload);
    }

    /// <summary>
    /// 长度域与实际帧长不一致时必须被拒绝。
    /// </summary>
    [Fact]
    public void Validate_Length_RejectsInconsistentLength()
    {
        var filter = new DeviceFrameFilter();

        // 长度域声明 10 字节，实际负载是 24 字节 —— 对不上
        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Heartbeat,
            """{"deviceId":"DRONE-001"}""",
            overrideLength: 10);

        Assert.Empty(FrameDecoder.Decode(filter, frame));
    }

    /// <summary>
    /// 长度域与实际帧长一致时必须能正常解出。
    /// </summary>
    [Fact]
    public void Validate_Length_AcceptsConsistentLength()
    {
        var filter = new DeviceFrameFilter();
        var json = """{"deviceId":"DRONE-001"}""";

        var packages = FrameDecoder.Decode(
            filter,
            FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, json));

        Assert.Equal(json, Assert.Single(packages).Payload);
    }

    // ------------------------------------------------------------------
    // 帧尾验证
    // ------------------------------------------------------------------

    /// <summary>
    /// 帧尾必须是 0xAA。
    /// </summary>
    [Fact]
    public void Validate_EndMark_MustBeEndMark()
    {
        var filter = new DeviceFrameFilter();

        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Heartbeat,
            """{"deviceId":"D1"}""",
            overrideEndMark: 0xBB);

        Assert.Empty(FrameDecoder.Decode(filter, frame));
    }

    /// <summary>
    /// JSON 负载里含 0xAA 字节时不能被截断 —— 帧边界由长度域决定，不由 0xAA 决定。
    /// </summary>
    [Fact]
    public void Validate_EndMark_PayloadContainingEndMarkByteIsNotTruncated()
    {
        var filter = new DeviceFrameFilter();

        // 负载里塞一个原始 0xAA 字节
        var json = """{"deviceId":"D1","blob":"AB""";
        var payload = Encoding.UTF8.GetBytes(json)
            .Concat(new byte[] { DeviceFrame.EndMark })
            .ToArray();

        var frame = FrameDecoder.BuildFrame(DeviceMessageTypes.Report, payload);

        var packages = FrameDecoder.Decode(filter, frame);

        var package = Assert.Single(packages);
        Assert.Equal(DeviceMessageTypes.Report, package.Key);

        // 负载里的原始 0xAA 字节：单独一个 0xAA 不是合法 UTF-8，
        // 解码后会成为替换字符 U+FFFD。关键是帧没有被截断 —— "AB" 之后的内容还在。
        Assert.Contains("AB", package.Payload);
        Assert.True(package.Payload.Length > json.Length, "负载被截断了");
    }

    // ------------------------------------------------------------------
    // 三项验证的综合：脏数据不得导致卡死或断连
    // ------------------------------------------------------------------

    /// <summary>
    /// 伪造的长度域（声明很大但实际很短）之后，真实帧仍能被正确解析。
    /// 这验证了"跳过坏标记后继续重同步"这条路径。
    /// </summary>
    [Fact]
    public void Validate_Combined_ResyncsAfterForgedLength()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(DeviceMessageTypes.Position, """{"deviceId":"D1","lat":30.1}""");

        // 先发一个长度域造假的帧（声明 999999 字节）
        var forged = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Heartbeat,
            """{"deviceId":"BAD"}""",
            overrideLength: 999999);

        // 再发一个真帧
        var buffer = forged.Concat(frame).ToArray();

        var packages = FrameDecoder.Decode(filter, buffer);

        // 只有真帧被解出，伪造的那个被丢弃
        var package = Assert.Single(packages);
        Assert.Equal(DeviceMessageTypes.Position, package.Key);
        Assert.Contains("D1", package.Payload);
        Assert.DoesNotContain("BAD", package.Payload);
    }

    /// <summary>
    /// 帧头、长度、帧尾三者都坏的帧必须被丢弃，且不影响后续帧。
    /// </summary>
    [Fact]
    public void Validate_Combined_DiscardsFullyInvalidFrame()
    {
        var filter = new DeviceFrameFilter();
        var good = FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"D1"}""");

        // 帧头错 + 长度错 + 帧尾错
        var bad = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Heartbeat,
            """{"deviceId":"X"}""",
            overrideLength: 999,
            overrideEndMark: 0x00,
            overrideBeginMark: 0x11);

        var buffer = bad.Concat(good).ToArray();

        var packages = FrameDecoder.Decode(filter, buffer);

        var package = Assert.Single(packages);
        Assert.Equal(DeviceMessageTypes.Heartbeat, package.Key);
    }

    /// <summary>
    /// 三项验证失败时一律不抛异常。
    /// </summary>
    /// <remarks>
    /// SuperSocket 把 filter 抛出的异常当作协议错误，直接以
    /// <c>CloseReason.ProtocolError</c> 关闭连接。脏数据必须能被"消化"掉，
    /// 而不是把连接打断。
    /// </remarks>
    [Fact]
    public void Validate_NeverThrowsProtocolException()
    {
        var filter = new DeviceFrameFilter();

        var candidates = new[]
        {
            // 帧头非法
            FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"D1"}""", overrideBeginMark: 0x00),
            // 帧尾非法
            FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"D1"}""", overrideEndMark: 0x00),
            // 长度越界
            FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"D1"}""", overrideLength: uint.MaxValue),
            // 三项全坏
            FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"D1"}""", overrideLength: 999, overrideEndMark: 0x00, overrideBeginMark: 0x11),
        };

        foreach (var frame in candidates)
        {
            var exception = Record.Exception(() =>
            {
                FrameDecoder.Decode(filter, frame);
            });

            Assert.Null(exception);
        }
    }
}
