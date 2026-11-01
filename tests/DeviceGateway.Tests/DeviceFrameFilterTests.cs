using System.Buffers;
using System.Text;
using DeviceGateway.Shared.Protocol;
using DeviceGateway.Tests.Support;
using SuperSocket.ProtoBase;

namespace DeviceGateway.Tests;

/// <summary>
/// 拆包过滤器的行为测试。
/// <para>
/// 这是整个项目最关键的一块：协议是地基，拆包错了后面全是错的。
/// 覆盖了完整帧、逐字节分片、粘包、脏字节重同步、帧尾校验、长度越界等场景。
/// </para>
/// <para>
/// 所有用例都通过 <see cref="FrameDecoder"/> 喂数据 —— 它严格模拟 SuperSocket 框架的读循环，
/// 所以这些用例同时也在验证"过滤器满足框架调用契约"。
/// </para>
/// </summary>
public class DeviceFrameFilterTests
{
    /// <summary>
    /// 完整帧能被正确解出。
    /// </summary>
    [Fact]
    public void Filter_DecodesCompleteFrame()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Heartbeat,
            """{"deviceId":"DRONE-001"}""");

        var packages = FrameDecoder.Decode(filter, frame);

        var package = Assert.Single(packages);
        Assert.Equal(DeviceMessageTypes.Heartbeat, package.Key);
        Assert.Equal("""{"deviceId":"DRONE-001"}""", package.Payload);
    }

    /// <summary>
    /// 消息类型字节要能正确解析出来 —— 命令路由全靠它。
    /// </summary>
    [Theory]
    [InlineData(DeviceMessageTypes.Heartbeat)]
    [InlineData(DeviceMessageTypes.Position)]
    [InlineData(DeviceMessageTypes.Report)]
    public void Filter_ReadsMessageType(byte messageType)
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(messageType, """{"deviceId":"D1"}""");

        var packages = FrameDecoder.Decode(filter, frame);

        Assert.Equal(messageType, Assert.Single(packages).Key);
    }

    /// <summary>
    /// 逐字节喂入：TCP 把一个帧拆成很多段到达时，必须能拼回来。
    /// </summary>
    /// <remarks>
    /// 模拟的是 SuperSocket 的真实行为：缓冲区里的数据不足时，
    /// filter 返回 null 且<b>不消费任何字节</b>，框架会把已收到的数据留在管道里，
    /// 下次读到更多数据后，用<b>累积后的完整缓冲区</b>再次调用 filter。
    /// </remarks>
    [Fact]
    public void Filter_HandlesFragmentedFrameByteByByte()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Position,
            """{"deviceId":"DRONE-001","lat":30.1,"lng":120.2}""");

        var packages = FrameDecoder.DecodeInChunks(filter, frame, 1);

        var package = Assert.Single(packages);
        Assert.Equal(DeviceMessageTypes.Position, package.Key);
        Assert.Contains("DRONE-001", package.Payload);
    }

    /// <summary>
    /// 不同的分片粒度都要能拼回完整帧。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(11)]
    public void Filter_HandlesFragmentedFrameInChunks(int chunkSize)
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Position,
            """{"deviceId":"DRONE-001","lat":30.1,"lng":120.2}""");

        var packages = FrameDecoder.DecodeInChunks(filter, frame, chunkSize);

        var package = Assert.Single(packages);
        Assert.Equal(DeviceMessageTypes.Position, package.Key);
        Assert.Contains("DRONE-001", package.Payload);
    }

    /// <summary>
    /// 数据不足时一个字节都不能消费 —— 否则半帧会被丢掉，永远拼不回来。
    /// </summary>
    /// <remarks>
    /// 框架的规则是：过滤器返回 null 时，框架会把已消费的字节从累积缓冲里移除。
    /// 所以"消费 0 字节"不是优化，而是硬性要求。
    /// </remarks>
    [Fact]
    public void Filter_DoesNotConsumeWhenFrameIncomplete()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Position,
            """{"deviceId":"DRONE-001","lat":30.1}""");

        // 只给前一半
        var half = frame.Take(frame.Length / 2).ToArray();

        var call = FrameDecoder.Call(filter, half);

        Assert.Null(call.Package);
        Assert.Equal(0, call.Consumed);
    }

    /// <summary>
    /// 帧头都不完整时同样不能消费。
    /// </summary>
    [Fact]
    public void Filter_DoesNotConsumeWhenHeaderIncomplete()
    {
        var filter = new DeviceFrameFilter();
        var partial = new byte[] { DeviceFrame.BeginMark, 0x00, 0x00 };

        var call = FrameDecoder.Call(filter, partial);

        Assert.Null(call.Package);
        Assert.Equal(0, call.Consumed);
    }

    /// <summary>
    /// 粘包：一次收到多个帧，必须能全部解出来。
    /// </summary>
    [Fact]
    public void Filter_HandlesCoalescedFrames()
    {
        var filter = new DeviceFrameFilter();
        var frame1 = FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"D1"}""");
        var frame2 = FrameDecoder.BuildFrame(DeviceMessageTypes.Position, """{"deviceId":"D2"}""");
        var frame3 = FrameDecoder.BuildFrame(DeviceMessageTypes.Report, """{"deviceId":"D3"}""");

        var buffer = frame1.Concat(frame2).Concat(frame3).ToArray();

        var packages = FrameDecoder.Decode(filter, buffer);

        Assert.Equal(3, packages.Count);
        Assert.Equal(DeviceMessageTypes.Heartbeat, packages[0].Key);
        Assert.Equal(DeviceMessageTypes.Position, packages[1].Key);
        Assert.Equal(DeviceMessageTypes.Report, packages[2].Key);
    }

    /// <summary>
    /// 脏字节重同步：帧前面有垃圾数据时，要能跳过垃圾找到真正的帧，且不能断连。
    /// </summary>
    [Fact]
    public void Filter_ResyncsAfterGarbageBytes()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"D1"}""");

        // 垃圾字节里故意也放一个 0x55，考验"跳过第一个假标记后还能找到真帧"。
        var garbage = new byte[] { 0x01, 0x02, DeviceFrame.BeginMark, 0x03, 0x04 };
        var buffer = garbage.Concat(frame).ToArray();

        var packages = FrameDecoder.Decode(filter, buffer);

        var package = Assert.Single(packages);
        Assert.Equal(DeviceMessageTypes.Heartbeat, package.Key);
        Assert.Equal("""{"deviceId":"D1"}""", package.Payload);
    }

    /// <summary>
    /// 假的 0x55 后面跟一个"看起来合法"的小长度时，必须能识别出帧尾不匹配并继续往后找，
    /// 而不是把错位的数据当成正常帧。
    /// </summary>
    [Fact]
    public void Filter_ResyncsWhenFalseMarkerHasPlausibleLength()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"D1"}""");

        // 假标记后面跟一个长度=2 的"合法"长度域，接着是真正的帧。
        var buffer = new byte[] { DeviceFrame.BeginMark, 0x00, 0x00, 0x00, 0x02, 0xFF }
            .Concat(frame)
            .ToArray();

        var packages = FrameDecoder.Decode(filter, buffer);

        var package = Assert.Single(packages);
        Assert.Equal(DeviceMessageTypes.Heartbeat, package.Key);
        Assert.Equal("""{"deviceId":"D1"}""", package.Payload);
    }

    /// <summary>
    /// 帧尾不是 0xAA 时必须被拒绝（长度域与内容不符），且不能把它当正常数据。
    /// </summary>
    [Fact]
    public void Filter_RejectsFrameWithBadEndMark()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Heartbeat,
            """{"deviceId":"D1"}""",
            overrideEndMark: 0xBB);

        var packages = FrameDecoder.Decode(filter, frame);

        // 不应该解出任何合法包（帧尾坏掉后，后面没有完整的下一帧）
        Assert.Empty(packages);
    }

    /// <summary>
    /// JSON 里包含 0xAA 字节时不能被截断 —— 这是按长度定位帧边界相对于
    /// BeginEndMarkPipelineFilter 的核心优势。
    /// </summary>
    [Fact]
    public void Filter_HandlesPayloadContainingEndMarkByte()
    {
        var filter = new DeviceFrameFilter();

        // 负载里含一个原始 0xAA 字节 —— 二进制内容出现在 JSON 负载里的情况。
        var payload = Encoding.UTF8.GetBytes("""{"deviceId":"D1","blob":"AB""")
            .Concat(new byte[] { DeviceFrame.EndMark })
            .ToArray();

        var frame = FrameDecoder.BuildFrame(DeviceMessageTypes.Report, payload);

        var packages = FrameDecoder.Decode(filter, frame);

        var package = Assert.Single(packages);
        Assert.Equal(DeviceMessageTypes.Report, package.Key);
        Assert.Contains("AB", package.Payload);
    }

    /// <summary>
    /// 长度域超出上限时必须拒绝 —— 防止伪造长度导致巨额内存分配。
    /// </summary>
    [Fact]
    public void Filter_RejectsOversizedPayloadLength()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Heartbeat,
            """{"deviceId":"D1"}""",
            overrideLength: 0x7FFFFFFF);

        var packages = FrameDecoder.Decode(filter, frame);

        Assert.Empty(packages);
    }

    /// <summary>
    /// 跨段的 ReadOnlySequence（真实 TCP 缓冲区分段的情况）必须能正确解析。
    /// </summary>
    [Fact]
    public void Filter_HandlesMultiSegmentSequence()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Position,
            """{"deviceId":"DRONE-001","lat":30.1}""");

        // 把帧切成三段，构造一个非连续的 ReadOnlySequence。
        // ReadOnlySequence 的多段构造要求各段通过 Next 串成链表。
        var segment1 = new TestSegment(frame.Take(5).ToArray(), 0);
        var segment2 = new TestSegment(frame.Skip(5).Take(10).ToArray(), segment1.Memory.Length);
        var segment3 = new TestSegment(frame.Skip(15).ToArray(), segment1.Memory.Length + segment2.Memory.Length);

        segment1.SetNext(segment2);
        segment2.SetNext(segment3);

        var sequence = new ReadOnlySequence<byte>(segment1, 0, segment3, segment3.Memory.Length);

        var reader = new SequenceReader<byte>(sequence);
        var package = filter.Filter(ref reader);

        Assert.NotNull(package);
        Assert.Equal(DeviceMessageTypes.Position, package!.Key);
        Assert.Contains("DRONE-001", package.Payload);
    }

    /// <summary>
    /// 用于构造非连续 ReadOnlySequence 的测试辅助段。
    /// </summary>
    private sealed class TestSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSegment(byte[] memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        /// <summary>
        /// 链到下一段。基类的 Next 只有 protected setter，测试里需要自己暴露一个设置方法。
        /// </summary>
        public void SetNext(ReadOnlySequenceSegment<byte> next) => Next = next;
    }

    /// <summary>
    /// 通过接口调用（框架的实际调用方式）也必须走本类的 Filter 实现。
    /// <para>
    /// 这条很重要：框架持有的是 <c>IPipelineFilter&lt;DevicePackage&gt;</c>，
    /// 通过接口分派。如果本类的 Filter 没有正确覆盖基类实现，
    /// 接口调用会走基类的逻辑 —— 而基类假定帧头在序列第 0 字节，
    /// 遇到前面的脏字节会永久卡住。
    /// </para>
    /// </summary>
    [Fact]
    public void Filter_ThroughInterface_UsesOwnImplementation()
    {
        IPipelineFilter<DevicePackage> filter = new DeviceFrameFilter();

        var frame = FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"D1"}""");
        var garbage = new byte[] { 0x01, 0x02, DeviceFrame.BeginMark, 0x03 };
        var buffer = garbage.Concat(frame).ToArray();

        // 通过接口调用，走的必须是本类的重同步实现。
        var packages = FrameDecoder.Decode(filter, buffer);

        var package = Assert.Single(packages);
        Assert.Equal(DeviceMessageTypes.Heartbeat, package.Key);
        Assert.Equal("""{"deviceId":"D1"}""", package.Payload);
    }

    /// <summary>
    /// 过滤器是无状态的：同一份数据反复解析，结果必须一致。
    /// </summary>
    /// <remarks>
    /// 这条锁死"不引入实例状态"这条设计约束。
    /// 框架只在成功解出包之后才调用 <c>Reset()</c>，
    /// 如果过滤器把"已找到帧头""整帧长度"存在字段里，脏数据路径下就会错乱。
    /// </remarks>
    [Fact]
    public void Filter_IsStateless_RepeatedCallsProduceSameResult()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"D1"}""");
        var garbage = new byte[] { 0x01, 0x02, 0x03 };
        var buffer = garbage.Concat(frame).ToArray();

        for (var i = 0; i < 5; i++)
        {
            var packages = FrameDecoder.Decode(filter, buffer);

            var package = Assert.Single(packages);
            Assert.Equal(DeviceMessageTypes.Heartbeat, package.Key);
        }
    }

    /// <summary>
    /// 任何畸形输入都不得抛出异常。
    /// </summary>
    /// <remarks>
    /// <b>这条是铁律</b>：SuperSocket 对 filter 抛异常的处理是
    /// 以 <c>CloseReason.ProtocolError</c> 关闭连接 —— 抛异常不是"容错"而是"断连"。
    /// </remarks>
    [Fact]
    public void Filter_NeverThrowsOnMalformedInput()
    {
        var filter = new DeviceFrameFilter();
        var random = new Random(20261101);

        for (var i = 0; i < 1000; i++)
        {
            var length = random.Next(0, 64);
            var buffer = new byte[length];
            random.NextBytes(buffer);

            // 不抛异常就是通过。这里显式断言一下，让失败信息更清楚。
            var exception = Record.Exception(() =>
            {
                FrameDecoder.Decode(filter, buffer);
            });

            Assert.Null(exception);
        }
    }

    /// <summary>
    /// 脏字节必须被及时消费掉，累积缓冲区才不会无限增长。
    /// </summary>
    /// <remarks>
    /// 框架的 <c>MaxPackageLength</c>（默认 1 MB）在过滤器消费 0 字节时，
    /// 量的是<b>整个累积缓冲区</b>的长度。脏字节如果不被吃掉，
    /// 缓冲区会一直涨到超过上限，然后被框架以协议错误断开。
    /// </remarks>
    [Fact]
    public void Filter_ConsumesGarbagePromptly_SoAccumulatedBufferCannotGrowUnbounded()
    {
        var filter = new DeviceFrameFilter();

        // 一堆不含 0x55 的垃圾字节。
        var garbage = Enumerable.Repeat((byte)0x41, 4096).ToArray();

        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(garbage));
        var package = filter.Filter(ref reader);

        Assert.Null(package);

        // 垃圾必须被全部消费掉。
        Assert.Equal(garbage.Length, reader.Consumed);
    }
}
