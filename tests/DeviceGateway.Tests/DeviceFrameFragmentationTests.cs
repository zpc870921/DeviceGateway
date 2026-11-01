using DeviceGateway.Shared.Protocol;
using DeviceGateway.Tests.Support;

namespace DeviceGateway.Tests;

/// <summary>
/// 分片 / 粘包 / 脏数据混合场景的测试。
///
/// <para>
/// 这些场景对应真实 TCP 流里最常见的问题：
/// 设备一帧数据被内核拆成多段到达、多个帧粘在一起到达、
/// 以及上一次连接残留的脏字节混进了新连接的首帧。
/// </para>
///
/// <para>
/// 全部通过 <see cref="FrameDecoder.DecodeInChunks"/> 驱动 ——
/// 它模拟框架"读一批、累积、重新喂入整个累积缓冲"的行为。
/// </para>
/// </summary>
public class DeviceFrameFragmentationTests
{
    /// <summary>
    /// 一帧拆成两半，先发前半，稍后再发后半 —— 必须能拼回完整帧。
    /// </summary>
    [Fact]
    public void Fragmentation_HalfFrameThenRest_DecodesAfterCompletion()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Heartbeat,
            """{"deviceId":"SPLIT-001"}""");

        var mid = frame.Length / 2;

        // 前半
        var first = FrameDecoder.DecodeInChunks(filter, frame[..mid], 1);
        Assert.Empty(first);

        // 后半补上（此时累积缓冲里有前半）
        var accumulated = frame.ToList();
        var packages = FrameDecoder.DecodeInChunks(filter, accumulated.ToArray(), 1);

        var package = Assert.Single(packages);
        Assert.Equal(DeviceMessageTypes.Heartbeat, package.Key);
        Assert.Equal("""{"deviceId":"SPLIT-001"}""", package.Payload);
    }

    /// <summary>
    /// 一帧拆成三段，每段隔一段时间到达 —— 必须能拼回完整帧。
    /// </summary>
    [Fact]
    public void Fragmentation_ThreeParts_DecodesAfterCompletion()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Position,
            """{"deviceId":"SPLIT-002","lat":30.1}""");

        var part1 = frame.Length / 3;
        var part2 = frame.Length * 2 / 3;

        // 第一段
        Assert.Empty(FrameDecoder.DecodeInChunks(filter, frame[..part1], 1));

        // 第二段（累积缓冲 = 第一段 + 第二段）
        var after2 = FrameDecoder.DecodeInChunks(filter, frame[..part2], 1);
        Assert.Empty(after2);

        // 第三段（累积缓冲 = 整帧）
        var after3 = FrameDecoder.DecodeInChunks(filter, frame, 1);

        var package = Assert.Single(after3);
        Assert.Equal(DeviceMessageTypes.Position, package.Key);
        Assert.Contains("SPLIT-002", package.Payload);
    }

    /// <summary>
    /// 两帧各自分半、交叉到达 —— 交错本身让字节流失去了帧边界，
    /// 但过滤器不会卡死，后面的正常帧仍然要能解出来。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这条记录的是<b>协议的实际行为</b>：
    /// 交错之后字节流里不再有"帧"的概念，
    /// 过滤器只能按"下一个 0x55 + 长度域 + 帧尾"去拼，
    /// 拼出来的东西有可能碰巧自洽。
    /// </para>
    /// <para>
    /// 真正要锁住的是：<b>流没有被卡死</b>，后面的正常帧照样解得出来。
    /// 现实中不会出现这种交错 —— TCP 是字节流，设备也是按帧顺序写出的。
    /// </para>
    /// </remarks>
    [Fact]
    public void Fragmentation_InterleavedFrames_KeepsStreamAlive()
    {
        var filter = new DeviceFrameFilter();
        var frame1 = FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"A"}""");
        var frame2 = FrameDecoder.BuildFrame(DeviceMessageTypes.Position, """{"deviceId":"B"}""");

        var mid1 = frame1.Length / 2;
        var mid2 = frame2.Length / 2;

        // 先到：帧1前半 + 帧2前半
        var firstHalf = frame1[..mid1].Concat(frame2[..mid2]).ToArray();

        // 后到：帧1后半 + 帧2后半
        var secondHalf = frame1[mid1..].Concat(frame2[mid2..]).ToArray();

        var interleaved = firstHalf.Concat(secondHalf).ToArray();

        // 后面再跟一个完好的帧，验证流没有被卡死。
        var good = FrameDecoder.BuildFrame(DeviceMessageTypes.Report, """{"deviceId":"C"}""");
        var buffer = interleaved.Concat(good).ToArray();

        var packages = FrameDecoder.DecodeInChunks(filter, buffer, 1);

        // 关键断言：最后一个完好帧必须解出来 —— 流没有被卡死。
        var last = packages[^1];
        Assert.Equal(DeviceMessageTypes.Report, last.Key);
        Assert.Equal("""{"deviceId":"C"}""", last.Payload);
    }

    /// <summary>
    /// 两帧<b>顺序</b>分片到达（先完整的一帧的前半，再完整的一帧的前半，最后两帧的后半）——
    /// 这种不会交错字节，帧边界是完好的，两帧都必须能解出。
    /// </summary>
    /// <remarks>
    /// 这是"粘包 + 分片同时出现"里真正会发生的形态：
    /// 设备把两帧一起写出去，TCP 把它们切成了若干段。
    /// 每一帧的字节仍然是连续的，只是到达的时机被切开了。
    /// </remarks>
    [Fact]
    public void Fragmentation_TwoFramesSplitAcrossReads_DecodesBoth()
    {
        var filter = new DeviceFrameFilter();
        var frame1 = FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"A"}""");
        var frame2 = FrameDecoder.BuildFrame(DeviceMessageTypes.Position, """{"deviceId":"B"}""");

        // 两帧连在一起写出去，被切成 5 字节一段到达。
        var buffer = frame1.Concat(frame2).ToArray();

        var packages = FrameDecoder.DecodeInChunks(filter, buffer, 5);

        Assert.Equal(2, packages.Count);
        Assert.Equal(DeviceMessageTypes.Heartbeat, packages[0].Key);
        Assert.Equal(DeviceMessageTypes.Position, packages[1].Key);
        Assert.Equal("""{"deviceId":"A"}""", packages[0].Payload);
        Assert.Equal("""{"deviceId":"B"}""", packages[1].Payload);
    }

    /// <summary>
    /// 半帧 + 垃圾字节 + 剩余半帧 —— 被污染的那一帧必须被丢弃，
    /// 但流不能因此卡死，后续的帧仍然要能解出来。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>注意这里为什么解不出原来那一帧</b>：垃圾字节插在帧的中间，
    /// 而长度域声明的负载长度没变 ⇒ 帧尾对不上 ⇒ 这一帧在协议层面已经损坏，
    /// 丢弃它是<b>正确</b>行为。TCP 不保留帧边界，设备往自己帧中间塞字节
    /// 就是把这一帧写坏了。
    /// </para>
    /// <para>
    /// 关键在于：<b>丢弃一帧不能让整条连接报废</b>。
    /// 旧实现（继承 FixedHeaderPipelineFilter）在这里会永久卡死。
    /// </para>
    /// </remarks>
    [Fact]
    public void Fragmentation_GarbageBetweenHalfFrames_DiscardsCorruptedFrameAndKeepsStreamAlive()
    {
        var filter = new DeviceFrameFilter();
        var corrupted = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Heartbeat,
            """{"deviceId":"SPLIT-003"}""");

        var mid = corrupted.Length / 2;
        var garbage = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        // 半帧 + 垃圾 + 剩余半帧 —— 这一帧在链路上已经损坏。
        var damaged = corrupted[..mid]
            .Concat(garbage)
            .Concat(corrupted[mid..])
            .ToArray();

        // 后面再跟一个完好的帧。
        var good = FrameDecoder.BuildFrame(DeviceMessageTypes.Position, """{"deviceId":"SPLIT-003B"}""");
        var buffer = damaged.Concat(good).ToArray();

        var packages = FrameDecoder.DecodeInChunks(filter, buffer, 1);

        // 损坏的那帧被丢弃，好的那帧照常解出 —— 流没有被卡死。
        var package = Assert.Single(packages);
        Assert.Equal(DeviceMessageTypes.Position, package.Key);
        Assert.Equal("""{"deviceId":"SPLIT-003B"}""", package.Payload);
    }

    /// <summary>
    /// 脏字节在帧之前 —— 必须能跳过脏字节找到真帧。
    /// </summary>
    [Fact]
    public void Fragmentation_GarbageBeforeFrame_Resyncs()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Heartbeat,
            """{"deviceId":"SPLIT-004"}""");

        var garbage = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var buffer = garbage.Concat(frame).ToArray();

        var packages = FrameDecoder.DecodeInChunks(filter, buffer, 1);

        var package = Assert.Single(packages);
        Assert.Equal("""{"deviceId":"SPLIT-004"}""", package.Payload);
    }

    /// <summary>
    /// 脏字节在完整帧之后 —— 后续帧仍然必须能解出。
    /// </summary>
    /// <remarks>
    /// 这条锁死"脏数据不污染后续帧"。旧实现下，一旦出现垃圾，
    /// 后面<b>所有</b>帧都会永久丢失。
    /// </remarks>
    [Fact]
    public void Fragmentation_GarbageAfterCompleteFrame_NextFrameStillDecodes()
    {
        var filter = new DeviceFrameFilter();
        var frame1 = FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, """{"deviceId":"A"}""");
        var frame2 = FrameDecoder.BuildFrame(DeviceMessageTypes.Position, """{"deviceId":"B"}""");

        var garbage = new byte[] { 0xFF, 0xFE, 0xFD };

        var buffer = frame1
            .Concat(garbage)
            .Concat(frame2)
            .ToArray();

        var packages = FrameDecoder.DecodeInChunks(filter, buffer, 1);

        Assert.Equal(2, packages.Count);
        Assert.Equal("""{"deviceId":"A"}""", packages[0].Payload);
        Assert.Equal("""{"deviceId":"B"}""", packages[1].Payload);
    }

    /// <summary>
    /// 连续垃圾字节之后，真帧仍然能解出。
    /// </summary>
    [Fact]
    public void Fragmentation_ContinuousGarbage_DoesNotBreakStream()
    {
        var filter = new DeviceFrameFilter();
        var frame = FrameDecoder.BuildFrame(
            DeviceMessageTypes.Heartbeat,
            """{"deviceId":"SPLIT-005"}""");

        var garbage = Enumerable.Repeat((byte)0x41, 1000).ToArray();
        var buffer = garbage.Concat(frame).ToArray();

        var packages = FrameDecoder.DecodeInChunks(filter, buffer, 1);

        var package = Assert.Single(packages);
        Assert.Equal("""{"deviceId":"SPLIT-005"}""", package.Payload);
    }

    /// <summary>
    /// 分片到达时，设备标识必须在补齐之后才能被正确读出 ——
    /// 中途不能读出半个 deviceId。
    /// </summary>
    [Fact]
    public void Fragmentation_PartialFrame_DoesNotProducePartialPayload()
    {
        var filter = new DeviceFrameFilter();
        var json = """{"deviceId":"SPLIT-006"}""";
        var frame = FrameDecoder.BuildFrame(DeviceMessageTypes.Heartbeat, json);

        // 逐字节喂，每次都检查：没喂完之前不能解出任何包。
        var accumulated = new List<byte>();
        var decoded = 0;

        for (var i = 0; i < frame.Length; i++)
        {
            accumulated.Add(frame[i]);

            var packages = FrameDecoder.Decode(
                filter,
                accumulated.ToArray());

            decoded += packages.Count;

            if (i < frame.Length - 1)
            {
                Assert.Empty(packages);
            }
        }

        Assert.Equal(1, decoded);
    }
}
