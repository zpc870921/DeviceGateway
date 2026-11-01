using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using SuperSocket.ProtoBase;

// 本文件的 Filter 按 SuperSocket 的契约返回 null 表示"数据不足"，
// 而框架的 IPipelineFilter<T>.Filter 签名对可空性是 oblivious 的。
// 框架自带的 FixedHeaderPipelineFilter 等同样是这么写的，
// 所以这里关闭可空性检查，避免无意义的 CS8603 警告。
#nullable disable

namespace DeviceGateway.Shared.Protocol;

/// <summary>
/// 设备协议的拆包过滤器：把 TCP 字节流还原成一个一个的 <see cref="DevicePackage"/>。
///
/// <para><b>帧格式</b>：<c>0x55 | LEN(4字节大端) | TYPE(1字节) | JSON(n字节) | 0xAA</c>，
/// 其中 LEN 仅为 JSON 负载的字节数 n。</para>
///
/// <para><b>为什么不用框架自带的 FixedHeaderPipelineFilter</b>：
/// 那是一个"定长帧头 + 长度域给出帧体长度"的过滤器，它的三条设计前提与本协议冲突：</para>
/// <list type="number">
///   <item>它把帧头当作<b>当前位置的固定长度前缀</b>（<c>reader.Sequence.Slice(0, headerSize)</c>），
///   没有"扫描下一个 0x55"的能力 ⇒ 流里粘着脏字节时无法重同步。</item>
///   <item>它的长度钩子以<b>负返回值</b>表示"致命错误"，框架收到就抛 <see cref="ProtocolException"/> ⇒
///   <b>连接被关闭</b>。而本协议里"长度域非法"只是脏数据，必须重同步而不是断连。</item>
///   <item>它把"已找到帧头"和"整帧长度"存在实例字段里，
///   而框架只在<b>成功解出包之后</b>才调用 <c>Reset()</c> ⇒ 脏数据路径下状态会错乱。</item>
/// </list>
///
/// <para><b>本实现的约定</b>（全部对齐 SuperSocket 的读循环契约）：</para>
/// <list type="bullet">
///   <item><b>无状态</b>：每次调用都从 reader 当前位置重新解析，
///   天然免疫"框架在解出整帧后才调 Reset()"的差异。</item>
///   <item><b>绝不抛异常</b>：脏数据只做丢弃与重同步。抛异常会导致 SuperSocket 以
///   <c>CloseReason.ProtocolError</c> 关闭连接 —— 那不是"容错"而是"断连"。</item>
///   <item><b>数据不足时消费 0 字节</b>：框架会把已消费的字节丢弃，
///   半消费会让已收到的部分帧永远拼不回来。</item>
///   <item><b>帧边界由长度域决定</b>，帧尾字节只做校验、不参与查找 ——
///   这样 JSON 里出现 0xAA 字节也不会截断。</item>
/// </list>
/// </summary>
public sealed class DeviceFrameFilter : IPipelineFilter<DevicePackage>
{
    /// <summary>
    /// 单帧 JSON 负载的最大字节数。长度域是外部输入，必须自校验上限，
    /// 否则一个伪造的巨大长度会导致巨额内存分配。
    /// </summary>
    public const int MaxPayloadLength = 64 * 1024;

    /// <summary>
    /// 本过滤器自行完成解析，不使用框架注入的 <see cref="Decoder"/>。
    /// </summary>
    public IPackageDecoder<DevicePackage> Decoder { get; set; } = null!;

    /// <summary>
    /// 过滤器链的下一个节点。本过滤器是链路末端，保持为 null。
    /// </summary>
    public IPipelineFilter<DevicePackage> NextFilter { get; private set; } = null!;

    /// <summary>
    /// 过滤器上下文。
    /// </summary>
    public object Context { get; set; }

    /// <summary>
    /// 重置过滤器状态。本实现无状态，因此为空操作。
    /// </summary>
    public void Reset()
    {
        // 无状态实现：不需要清理任何东西。
    }

    /// <summary>
    /// 从字节流中取出下一个完整帧。
    /// </summary>
    /// <param name="reader">指向待解析数据的 reader。</param>
    /// <returns>
    /// 解析出完整帧时返回消息包；数据不足或存在脏字节时返回 null。
    /// 数据不足时<b>不消费任何字节</b>（保留已收到的部分帧等待下次）；
    /// 遇到脏字节时<b>消费掉脏字节</b>（否则脏字节会一直留在缓冲区里，永远无法前进）。
    /// </returns>
    public DevicePackage Filter(ref SequenceReader<byte> reader)
    {
        // 记录本次调用开始时游标的位置。数据不足时要退回这里。
        var start = reader.Consumed;

        // 依次尝试候选的帧起始标记。
        // 每找到一个 0x55，就试着按它解析一帧；解析不成就把这个 0x55 当脏字节跳过，
        // 继续找下一个 0x55。这样天然支持重同步。
        while (true)
        {
            // 1. 从当前位置找下一个帧起始标记 0x55。
            var markerOffset = FindBeginMark(ref reader);
            if (markerOffset < 0)
            {
                // 整个缓冲区都没有帧起始标记 —— 全部是脏字节，丢弃后等待更多数据。
                //
                // 注意此时游标已经推进到缓冲区末尾（脏字节被消费掉了），这是正确的：
                // 框架会把过滤器消费掉的字节从累积缓冲里移除，脏字节必须被吃掉，
                // 否则会一直留在缓冲区里让解析卡住。
                return null;
            }

            // 从标记开始的可用字节数。
            var available = reader.Remaining;

            // 2. 帧起始标记之后还需要 4 字节长度 + 1 字节消息类型，
            //    加上标记本身共 6 字节（= DeviceFrame.HeaderSize）。
            //    差一个字节都不行 —— 否则后面读长度/类型时会越界。
            if (available < DeviceFrame.HeaderSize)
            {
                // 帧头还没收全 —— 退回本次调用的起点，一个字节都不消费，等下次。
                RewindTo(ref reader, start);
                return null;
            }

            // 读取长度域（大端序）与消息类型。
            //
            // 帧内偏移：0x55 在 0，长度域在 1~4，消息类型在 5，JSON 从 6 开始。
            // markerOffset 是帧起始标记的绝对偏移。
            var lengthOffset = markerOffset + 1;
            var payloadLength = ReadUInt32BigEndianAt(reader.Sequence, lengthOffset);
            var messageType = ReadByteAt(reader.Sequence, lengthOffset + DeviceFrame.LengthFieldSize);

            // 3. 校验长度域。必须在分配任何内存之前判断。
            if (payloadLength > MaxPayloadLength)
            {
                // 长度域不可信 —— 这个 0x55 是脏数据。跳过它，继续找下一个 0x55。
                SkipMarker(ref reader);
                continue;
            }

            // 4. 需要 payloadLength 字节的 JSON + 1 字节帧尾。
            var frameLength = DeviceFrame.TotalLength((int)payloadLength);
            if (available < frameLength)
            {
                // 负载还没收全 —— 退回本次调用的起点不消费，让已收到的部分帧留在缓冲区里等待。
                RewindTo(ref reader, start);
                return null;
            }

            // 5. 校验帧尾标记。按长度定位到固定偏移比对，而不是搜索 0xAA。
            var endMarkOffset = markerOffset + DeviceFrame.HeaderSize + (int)payloadLength;
            var endMark = ReadByteAt(reader.Sequence, endMarkOffset);
            if (endMark != DeviceFrame.EndMark)
            {
                // 帧尾不匹配 —— 长度域与实际内容不符，这个 0x55 是脏数据。
                // 跳过它，继续找下一个 0x55。
                SkipMarker(ref reader);
                continue;
            }

            // 6. 一切正常，取出 JSON 负载，并把游标推进到帧尾之后。
            var payloadStart = markerOffset + DeviceFrame.HeaderSize;
            var payload = reader.Sequence.Slice(payloadStart, payloadLength);
            var json = Encoding.UTF8.GetString(payload);

            // 消费到帧尾之后。
            // 此时游标正好停在帧起始标记处（FindBeginMark 用的是 TryPeek，没有消费标记本身），
            // 所以推进 frameLength 字节正好跳过整帧。
            reader.Advance(frameLength);

            return new DevicePackage
            {
                Key = messageType,
                Payload = json
            };
        }
    }

    /// <summary>
    /// 从 reader 当前位置开始查找帧起始标记 0x55，跳过前面的脏字节。
    /// </summary>
    /// <param name="reader">reader。</param>
    /// <returns>
    /// 找到时返回标记的绝对偏移（游标停在标记上，未消费它）；没找到返回 -1。
    /// </returns>
    private static long FindBeginMark(ref SequenceReader<byte> reader)
    {
        while (reader.Remaining > 0)
        {
            // TryPeek 看当前位置的字节但不消费它。
            if (reader.TryPeek(out var current) && current == DeviceFrame.BeginMark)
            {
                return reader.Consumed;
            }

            // 不是帧起始标记 —— 丢弃这一个字节，继续往下找。
            reader.Advance(1);
        }

        return -1;
    }

    /// <summary>
    /// 跳过当前的帧起始标记（它被判定为脏数据），继续往后扫描。
    /// </summary>
    /// <param name="reader">reader。</param>
    private static void SkipMarker(ref SequenceReader<byte> reader)
    {
        // 游标此刻停在标记字节上，消费掉它即可，
        // 下一次 FindBeginMark 就会从下一个字节开始找。
        reader.Advance(1);
    }

    /// <summary>
    /// 把 reader 的游标退回指定绝对偏移。
    /// </summary>
    /// <param name="reader">reader。</param>
    /// <param name="offset">目标绝对偏移。</param>
    private static void RewindTo(ref SequenceReader<byte> reader, long offset)
    {
        // SequenceReader.Rewind 会正确处理跨段的序列，不用自己逐段回退。
        reader.Rewind(reader.Consumed - offset);
    }

    /// <summary>
    /// 读取序列中某个绝对偏移上的单个字节。
    /// </summary>
    /// <param name="sequence">字节序列。</param>
    /// <param name="offset">绝对偏移。</param>
    /// <returns>该偏移上的字节值；越界时返回 0。</returns>
    /// <remarks>
    /// TCP 缓冲区的 ReadOnlySequence 可能是多段的（非连续内存），
    /// 所以不能直接用 FirstSpan()，那样只在单段时成立。这里用 SequenceReader 逐段推进，
    /// 无论分几段都能正确取到。
    /// <para>
    /// 调用方必须先确认该偏移在序列范围内 —— 越界时静默返回 0 会伪造出数据，
    /// 所以这里把"读不到"如实返回 0，由调用方的长度校验兜住。
    /// </para>
    /// </remarks>
    private static byte ReadByteAt(ReadOnlySequence<byte> sequence, long offset)
    {
        var reader = new SequenceReader<byte>(sequence);
        reader.Advance(offset);

        return reader.TryPeek(out var value) ? value : (byte)0;
    }

    /// <summary>
    /// 读取序列中某个绝对偏移起的 4 个字节（大端序长度域）。
    /// </summary>
    /// <param name="sequence">字节序列。</param>
    /// <param name="offset">绝对偏移。</param>
    /// <returns>长度域的数值；越界时返回 0。</returns>
    /// <remarks>
    /// 调用方必须先确认这 4 个字节在序列范围内。
    /// 这里如实返回读到的值（读不到时全 0），由调用方的长度上限校验兜住。
    /// </remarks>
    private static uint ReadUInt32BigEndianAt(ReadOnlySequence<byte> sequence, long offset)
    {
        Span<byte> buffer = stackalloc byte[DeviceFrame.LengthFieldSize];

        var reader = new SequenceReader<byte>(sequence);
        reader.Advance(offset);
        reader.TryCopyTo(buffer);

        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }
}
