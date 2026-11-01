using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using DeviceGateway.Shared.Protocol;
using SuperSocket.ProtoBase;

namespace DeviceGateway.Tests.Support;

/// <summary>
/// 模拟 SuperSocket 框架的读循环，把字节流喂给过滤器。
///
/// <para>
/// 这里的实现逐行对照 <c>SuperSocket.Connection.PipeConnectionBase.ReadBuffer</c>
/// （SuperSocket 2.1.0 反编译所得），严格遵守三条关键契约：
/// </para>
/// <list type="number">
///   <item>每轮循环<b>新建</b> <see cref="SequenceReader{T}"/>，作用于累积缓冲的当前切片。</item>
///   <item>解出包之后才调用 <c>filter.Reset()</c>；返回 null 时不调用。</item>
///   <item>返回 null 时<b>必须消费 0 字节</b> —— 框架会把已消费的字节从累积缓冲里丢弃
///   （<c>reader.AdvanceTo(buffer.GetPosition(Consumed), buffer.End)</c>），
///   半消费会让已收到的部分帧永远拼不回来。</item>
/// </list>
///
/// <para>
/// 第 3 条要分两种情况：
/// <list type="bullet">
///   <item><b>数据不足</b>（帧没收全）⇒ 必须消费 <b>0 字节</b>。
///   已收到的部分帧必须留在累积缓冲里等下一批，否则永远拼不回来。</item>
///   <item><b>脏字节</b> ⇒ <b>必须消费掉</b>。
///   框架会把已消费的字节从累积缓冲里移除，脏字节留着只会让流永远卡住。</item>
/// </list>
/// 这两条都由过滤器自己保证（见 <c>DeviceFrameFilter</c> 的注释），
/// 本辅助方法只负责忠实复现框架的行为。
/// </para>
/// </summary>
internal static class FrameDecoder
{
    /// <summary>
    /// 把整个缓冲区一次性喂给过滤器，解出所有消息包。
    /// </summary>
    /// <param name="filter">过滤器。</param>
    /// <param name="buffer">字节缓冲区。</param>
    /// <returns>解出的消息包。</returns>
    internal static List<DevicePackage> Decode(
        IPipelineFilter<DevicePackage> filter,
        byte[] buffer)
    {
        return Decode(filter, buffer, out _);
    }

    /// <summary>
    /// 把整个缓冲区一次性喂给过滤器，解出所有消息包，并报告消费了多少字节。
    /// </summary>
    /// <param name="filter">过滤器。</param>
    /// <param name="buffer">字节缓冲区。</param>
    /// <param name="consumed">本次消费掉的字节数。</param>
    /// <returns>解出的消息包。</returns>
    /// <remarks>
    /// 消费数很重要：框架会把它从累积缓冲里切掉，
    /// 下一次读到的数据接在后面。分批喂入的辅助方法依赖它来推进偏移。
    /// </remarks>
    internal static List<DevicePackage> Decode(
        IPipelineFilter<DevicePackage> filter,
        byte[] buffer,
        out long consumed)
    {
        var packages = new List<DevicePackage>();

        // 累积缓冲：socket 里还没被读走的字节都留在这里。
        var accumulated = new ReadOnlySequence<byte>(buffer);

        consumed = 0;

        while (true)
        {
            // 框架每次迭代都 new 一个 reader —— 这正是旧测试搞错的地方。
            // 复用同一个 reader 会让基类从"已消费区域"重新切片，直接越界。
            var reader = new SequenceReader<byte>(accumulated);

            var package = filter.Filter(ref reader);

            var num = reader.Consumed;      // 本次 Filter 调用消费了多少

            if (package is null)
            {
                // 返回 null ⇒ 需要更多数据。
                //
                // 框架此刻会按 num 决定去留：
                //   num == 0 ⇒ 什么都不丢（reader.AdvanceTo(buffer.Start, buffer.End)）
                //   num  > 0 ⇒ 丢掉这 num 个字节（reader.AdvanceTo(GetPosition(Consumed), End)）
                //
                // 两种都是框架认可的行为：前者是"半帧留着等下一批"，
                // 后者是"脏字节吃掉"。这里如实复现，不额外加限制。
                consumed += num;
                break;
            }

            packages.Add(package);

            // Reset 只在产出包之后调用。
            filter.Reset();

            consumed += num;

            if (reader.End)
            {
                break;
            }

            if (num > 0)
            {
                accumulated = accumulated.Slice(num, accumulated.Length - num);
            }
        }

        return packages;
    }

    /// <summary>
    /// 分批喂入，模拟真实 socket 的多次读。
    /// </summary>
    /// <param name="filter">过滤器。</param>
    /// <param name="buffer">字节缓冲区。</param>
    /// <param name="chunkSize">每批的字节数。</param>
    /// <returns>解出的消息包。</returns>
    /// <remarks>
    /// 每喂一批就把<b>整个累积缓冲</b>重新喂一遍 —— 这正是框架的行为
    /// （过滤器返回 null 时，框架调用 <c>reader.AdvanceTo(buffer.Start, buffer.End)</c>，
    /// 也就是"这一轮什么都没消费"，下次读到的数据会追加在原有数据之后）。
    /// 如果只喂新到的那一批，就测不出"半帧能不能活到下一轮"这个关键问题。
    /// <para>
    /// 已经解出来的帧不会被重复解 —— 累积缓冲里只保留<b>还没被消费</b>的字节，
    /// 与框架用 <c>reader.AdvanceTo(GetPosition(Consumed), End)</c> 切缓冲的效果一致。
    /// </para>
    /// </remarks>
    internal static List<DevicePackage> DecodeInChunks(
        IPipelineFilter<DevicePackage> filter,
        byte[] buffer,
        int chunkSize)
    {
        var packages = new List<DevicePackage>();

        // 累积缓冲里还没被消费的字节。
        var pending = new List<byte>();

        for (var offset = 0; offset < buffer.Length; offset += chunkSize)
        {
            var take = Math.Min(chunkSize, buffer.Length - offset);

            for (var i = 0; i < take; i++)
            {
                pending.Add(buffer[offset + i]);
            }

            // 把整个累积缓冲喂进去 —— 已解出的帧不会重复解，
            // 因为 Decode 只报告"还没被消费"的部分。
            var decoded = Decode(filter, pending.ToArray(), out var consumed);

            packages.AddRange(decoded);

            // 框架会把消费掉的字节从累积缓冲里切掉。
            pending = pending.Skip((int)consumed).ToList();
        }

        return packages;
    }

    /// <summary>
    /// 分批喂入，并记录每一批之后的消费量。
    /// </summary>
    /// <param name="filter">过滤器。</param>
    /// <param name="buffer">字节缓冲区。</param>
    /// <param name="chunkSize">每批的字节数。</param>
    /// <returns>每一步的解码记录。</returns>
    internal static List<FrameDecodeStep> DecodeStepByStep(
        IPipelineFilter<DevicePackage> filter,
        byte[] buffer,
        int chunkSize)
    {
        var steps = new List<FrameDecodeStep>();
        var pending = new List<byte>();

        for (var offset = 0; offset < buffer.Length; offset += chunkSize)
        {
            var take = Math.Min(chunkSize, buffer.Length - offset);

            for (var i = 0; i < take; i++)
            {
                pending.Add(buffer[offset + i]);
            }

            var decoded = Decode(filter, pending.ToArray(), out var consumed);

            steps.Add(new FrameDecodeStep(
                steps.Count,
                pending.Count,
                decoded.Count));

            pending = pending.Skip((int)consumed).ToList();
        }

        return steps;
    }

    /// <summary>
    /// 构造一个完整帧。
    /// </summary>
    /// <param name="messageType">消息类型字节。</param>
    /// <param name="json">JSON 负载文本。</param>
    /// <returns>帧字节。</returns>
    internal static byte[] BuildFrame(byte messageType, string json)
    {
        return BuildFrame(messageType, Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// 构造一个完整帧（负载为原始字节，可塞入 0xAA 之类的特殊字节）。
    /// </summary>
    /// <param name="messageType">消息类型字节。</param>
    /// <param name="payload">负载原始字节。</param>
    /// <returns>帧字节。</returns>
    internal static byte[] BuildFrame(byte messageType, byte[] payload)
    {
        var frame = new byte[DeviceFrame.TotalLength(payload.Length)];

        frame[0] = DeviceFrame.BeginMark;
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(1, DeviceFrame.LengthFieldSize),
            (uint)payload.Length);
        frame[DeviceFrame.LengthFieldSize + 1] = messageType;
        payload.CopyTo(frame, DeviceFrame.HeaderSize);
        frame[DeviceFrame.EndMarkOffset(payload.Length)] = DeviceFrame.EndMark;

        return frame;
    }

    /// <summary>
    /// 构造一个可被"破坏"的完整帧，用来测三项验证。
    /// </summary>
    /// <param name="messageType">消息类型字节。</param>
    /// <param name="json">JSON 负载文本。</param>
    /// <param name="overrideLength">覆盖长度域（用来伪造长度）。</param>
    /// <param name="overrideEndMark">覆盖帧尾字节。</param>
    /// <param name="overrideBeginMark">覆盖帧起始标记。</param>
    /// <returns>帧字节。</returns>
    internal static byte[] BuildFrame(
        byte messageType,
        string json,
        uint? overrideLength = null,
        byte? overrideEndMark = null,
        byte? overrideBeginMark = null)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[DeviceFrame.TotalLength(payload.Length)];

        frame[0] = overrideBeginMark ?? DeviceFrame.BeginMark;
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(1, DeviceFrame.LengthFieldSize),
            overrideLength ?? (uint)payload.Length);
        frame[DeviceFrame.LengthFieldSize + 1] = messageType;
        payload.CopyTo(frame, DeviceFrame.HeaderSize);
        frame[DeviceFrame.EndMarkOffset(payload.Length)] = overrideEndMark ?? DeviceFrame.EndMark;

        return frame;
    }

    /// <summary>
    /// 调用一次过滤器并返回消费情况，用于断言"数据不足时必须消费 0 字节"。
    /// </summary>
    /// <param name="filter">过滤器。</param>
    /// <param name="buffer">字节缓冲区。</param>
    /// <returns>本次调用的结果。</returns>
    /// <remarks>
    /// 这条是框架契约里最容易被违反的一条：
    /// 过滤器返回 null 时如果消费了字节，框架会把那些字节丢掉，
    /// 已收到的部分帧就永远拼不回来了。
    /// </remarks>
    internal static FilterCall Call(
        IPipelineFilter<DevicePackage> filter,
        byte[] buffer)
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(buffer));

        var package = filter.Filter(ref reader);

        return new FilterCall(package, reader.Consumed, reader.End);
    }

    /// <summary>
    /// 一次过滤器调用的结果。
    /// </summary>
    /// <param name="Package">解出的消息包；数据不足或脏数据时为 null。</param>
    /// <param name="Consumed">本次消费的字节数。</param>
    /// <param name="ReachedEnd">缓冲区是否被完全消费。</param>
    internal sealed record FilterCall(
        DevicePackage? Package,
        long Consumed,
        bool ReachedEnd);

    /// <summary>
    /// 一批数据喂进去之后的解码记录。
    /// </summary>
    /// <param name="ChunkIndex">这是第几批（从 0 开始）。</param>
    /// <param name="AccumulatedLength">喂进去的累积缓冲区长度。</param>
    /// <param name="PackagesInChunk">这一批解出的消息包数量。</param>
    internal sealed record FrameDecodeStep(
        int ChunkIndex,
        int AccumulatedLength,
        int PackagesInChunk);
}
