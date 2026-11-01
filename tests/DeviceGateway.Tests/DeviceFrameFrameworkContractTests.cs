using System.Buffers;
using DeviceGateway.Shared.Protocol;
using DeviceGateway.Tests.Support;
using SuperSocket.ProtoBase;

namespace DeviceGateway.Tests;

/// <summary>
/// 过滤器与 SuperSocket 框架之间的<b>调用契约</b>测试。
///
/// <para>
/// 这一组用例不测"我们的过滤器解得对不对"（那是
/// <see cref="DeviceFrameFilterTests"/> 与 <see cref="DeviceFrameValidationTests"/> 的事），
/// 而是测"我们的过滤器是否满足框架的调用方式"。
/// </para>
///
/// <para>
/// 契约来自 <c>SuperSocket.Connection.PipeConnectionBase.ReadBuffer</c>
/// （SuperSocket 2.1.0 反编译所得），三条关键规则：
/// </para>
/// <list type="number">
///   <item>过滤器返回 null ⇒ 框架<b>重新喂入整个累积缓冲</b>。</item>
///   <item>过滤器返回 null 且消费了 N 个字节 ⇒ 框架<b>丢弃这 N 个字节</b>
///   （<c>reader.AdvanceTo(buffer.GetPosition(Consumed), buffer.End)</c>）。</item>
///   <item>过滤器<b>抛出异常</b> ⇒ 框架以 <c>CloseReason.ProtocolError</c> 关闭连接。</item>
/// </list>
///
/// <para>
/// 换句话说：<b>脏字节必须被吃掉，半帧必须被留下，两者都不能抛异常。</b>
/// </para>
/// </summary>
public class DeviceFrameFrameworkContractTests
{
    /// <summary>
    /// 数据不足时必须消费 0 字节 —— 半帧要留在累积缓冲里等下一批。
    /// </summary>
    [Fact]
    public void Framework_FilterReturningNull_ConsumesZeroBytes()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Position,
            """{"deviceId":"DRONE-001","lat":30.1}""");

        // 逐字节缩短，直到不足以构成完整帧。
        for (var length = 1; length < frame.Length; length++)
        {
            var call = FrameDecoder.Call(filter, frame[..length]);

            Assert.Null(call.Package);
            Assert.Equal(0, call.Consumed);
        }
    }

    /// <summary>
    /// 半帧必须能活到下一批数据 —— 不能被提前丢弃。
    /// </summary>
    [Fact]
    public void Framework_FilterReturningNull_DoesNotDropAlreadyReceivedBytes()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Heartbeat,
            """{"deviceId":"DRONE-001"}""");

        var mid = frame.Length / 2;

        // 前半单独喂：不解出包，也不消费。
        var firstCall = FrameDecoder.Call(filter, frame[..mid]);
        Assert.Null(firstCall.Package);
        Assert.Equal(0, firstCall.Consumed);

        // 补上后半：完整帧解出。
        var secondCall = FrameDecoder.Call(filter, frame);
        Assert.NotNull(secondCall.Package);
        Assert.Equal(frame.Length, secondCall.Consumed);
    }

    /// <summary>
    /// 任何畸形输入都不得抛出异常。
    /// </summary>
    /// <remarks>
    /// 抛异常 = 框架以 <c>CloseReason.ProtocolError</c> 断连。
    /// 这是"容错"与"断连"的分界线。
    /// </remarks>
    [Fact]
    public void Framework_FilterNeverThrows_AcrossGarbageCorpus()
    {
        var filter = new DeviceFrameFilter();
        var random = new Random(20261101);

        for (var i = 0; i < 2000; i++)
        {
            var length = random.Next(0, 128);
            var buffer = new byte[length];
            random.NextBytes(buffer);

            var exception = Record.Exception(() =>
            {
                FrameDecoder.Decode(filter, buffer);
            });

            Assert.Null(exception);
        }
    }

    /// <summary>
    /// 分片能跨多次读拼回。
    /// </summary>
    [Fact]
    public void Framework_FragmentedFrame_ReassemblesAcrossReads()
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
    /// 脏字节前缀之后仍能解出帧 —— 连接不会因为脏数据而废掉。
    /// </summary>
    [Fact]
    public void Framework_GarbagePrefixThenFrame_DeliversFrame()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"D1"}""");

        var garbage = new byte[] { 0x01, 0x02, DeviceFrame.BeginMark, 0x03, 0x04 };
        var buffer = garbage.Concat(frame).ToArray();

        var packages = FrameDecoder.DecodeInChunks(filter, buffer, 1);

        var package = Assert.Single(packages);
        Assert.Equal("""{"deviceId":"D1"}""", package.Payload);
    }

    /// <summary>
    /// 粘包按序全部解出。
    /// </summary>
    [Fact]
    public void Framework_CoalescedFrames_DeliversAllInOrder()
    {
        var filter = new DeviceFrameFilter();
        var frame1 = FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"A"}""");
        var frame2 = FrameDecoder.BuildFrame(DeviceMessageTypes.Position, """{"deviceId":"B"}""");
        var frame3 = FrameDecoder.BuildFrame(DeviceMessageTypes.Report, """{"deviceId":"C"}""");

        var buffer = frame1.Concat(frame2).Concat(frame3).ToArray();

        var packages = FrameDecoder.DecodeInChunks(filter, buffer, 3);

        Assert.Equal(3, packages.Count);
        Assert.Equal("""{"deviceId":"A"}""", packages[0].Payload);
        Assert.Equal("""{"deviceId":"B"}""", packages[1].Payload);
        Assert.Equal("""{"deviceId":"C"}""", packages[2].Payload);
    }

    /// <summary>
    /// 帧尾非法不得导致断连 —— 帧被丢弃，但流继续。
    /// </summary>
    [Fact]
    public void Framework_FrameWithBadEndMark_DoesNotCloseConnection()
    {
        var filter = new DeviceFrameFilter();

        var bad = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Heartbeat,
            """{"deviceId":"BAD"}""",
            overrideEndMark: 0xBB);

        var good = FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"GOOD"}""");

        var exception = Record.Exception(() =>
        {
            var packages = FrameDecoder.Decode(filter, bad.Concat(good).ToArray());

            // 坏帧被丢弃，好帧照常解出。
            var package = Assert.Single(packages);
            Assert.Equal("""{"deviceId":"GOOD"}""", package.Payload);
        });

        Assert.Null(exception);
    }

    /// <summary>
    /// 长度越界不得导致断连。
    /// </summary>
    [Fact]
    public void Framework_OversizedLength_DoesNotCloseConnection()
    {
        var filter = new DeviceFrameFilter();

        var bad = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Heartbeat,
            """{"deviceId":"BAD"}""",
            overrideLength: uint.MaxValue);

        var good = FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"GOOD"}""");

        var exception = Record.Exception(() =>
        {
            var packages = FrameDecoder.Decode(filter, bad.Concat(good).ToArray());

            var package = Assert.Single(packages);
            Assert.Equal("""{"deviceId":"GOOD"}""", package.Payload);
        });

        Assert.Null(exception);
    }

    /// <summary>
    /// 垃圾洪水下累积缓冲区不会无限增长。
    /// </summary>
    /// <remarks>
    /// 框架的 <c>MaxPackageLength</c>（默认 1 MB）在过滤器消费 0 字节时，
    /// 量的是<b>整个累积缓冲区</b>。脏字节如果不被吃掉，
    /// 缓冲区会一直涨到超过上限，然后被框架以协议错误断开。
    /// </remarks>
    [Fact]
    public void Framework_AccumulatedBuffer_StaysBoundedUnderGarbageFlood()
    {
        var filter = new DeviceFrameFilter();

        // 一大堆不含 0x55 的垃圾字节。
        var garbage = Enumerable.Repeat((byte)0x41, 100_000).ToArray();

        var call = FrameDecoder.Call(filter, garbage);

        Assert.Null(call.Package);

        // 垃圾必须被全部吃掉 —— 累积缓冲区才不会涨。
        Assert.Equal(garbage.Length, call.Consumed);
    }

    /// <summary>
    /// 合法帧不会超过框架的 <c>MaxPackageLength</c>（默认 1 MB）。
    /// </summary>
    /// <remarks>
    /// 我们自己的负载上限是 64 KB，帧长最多 64 KB + 6 字节，
    /// 远小于框架的 1 MB 上限 ⇒ 合法帧不会被框架误杀。
    /// </remarks>
    [Fact]
    public void Framework_MaxPackageLength_NotExceededByValidFrame()
    {
        const int frameworkMaxPackageLength = 1024 * 1024;   // ConnectionOptions.MaxPackageLength 默认值

        var filter = new DeviceFrameFilter();

        // 造一个正好 64 KB 的负载 —— 我们允许的最大值。
        var payload = new byte[DeviceFrameFilter.MaxPayloadLength];
        System.Text.Encoding.UTF8.GetBytes("""{"deviceId":"D1"}""").CopyTo(payload, 0);

        var frame = FrameDecoder.BuildFrame(DeviceMessageTypes.Report, payload);

        Assert.True(
            frame.Length < frameworkMaxPackageLength,
            $"合法帧长度 {frame.Length} 不应超过框架上限 {frameworkMaxPackageLength}");

        var packages = FrameDecoder.Decode(filter, frame);

        Assert.Single(packages);
    }

    /// <summary>
    /// <c>Reset()</c> 只在成功解出包之后被调用。
    /// </summary>
    /// <remarks>
    /// 框架的调用时机：解出包后调 <c>Reset()</c>；返回 null 时不调；
    /// 抛异常时也不调。所以过滤器必须是无状态的 ——
    /// 靠 <c>Reset()</c> 清理状态的实现在脏数据路径上会出错。
    /// </remarks>
    [Fact]
    public void Framework_ResetCalledOnlyAfterPackageProduced()
    {
        var counting = new CountingFilter();

        // 一帧不完整 ⇒ 返回 null ⇒ 不该调 Reset。
        FrameDecoder.Decode(counting, new byte[] { DeviceFrame.BeginMark, 0x00 });

        Assert.Equal(0, counting.ResetCount);

        // 一帧完整 ⇒ 解出包 ⇒ 该调 Reset。
        var frame = FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"D1"}""");

        var packages = FrameDecoder.Decode(counting, frame);

        Assert.Single(packages);
        Assert.Equal(1, counting.ResetCount);
    }

    /// <summary>
    /// 统计 <c>Reset()</c> 调用次数的过滤器包装器。
    /// </summary>
    private sealed class CountingFilter : IPipelineFilter<DevicePackage>
    {
        /// <summary>Reset 被调用的次数。</summary>
        public int ResetCount { get; private set; }

        /// <inheritdoc />
        public IPackageDecoder<DevicePackage> Decoder { get; set; } = null!;

        /// <inheritdoc />
        public IPipelineFilter<DevicePackage> NextFilter { get; private set; } = null!;

        /// <inheritdoc />
        public object Context { get; set; } = null!;

        /// <inheritdoc />
        public void Reset()
        {
            ResetCount++;
        }

        /// <inheritdoc />
        public DevicePackage Filter(ref SequenceReader<byte> reader)
        {
            var inner = new DeviceFrameFilter();
            return inner.Filter(ref reader);
        }
    }
}
