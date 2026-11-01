# 用 SuperSocket v2 做设备接入网关：一份以「跑得通」为准的实战记录

> 副标题：ASP.NET Core + SuperSocket v2 命令模式 + 自定义二进制协议，以及我在这个过程中做错和做对的事

---

## 写在前面：这篇文章想解决什么

做设备接入网关，SuperSocket v2 是个很好的起点——它把 TCP 服务器的骨架给你了：会话管理、管道化读写、命令路由、还能和 ASP.NET Core 共用一个宿主。

但真正花时间的从来不是"连上"，而是**连上之后如何不出事**：粘包半包怎么处理、脏字节怎么重同步、设备离线怎么判、并发写会不会把帧写坏、设备身份会不会被劫持。

这篇文章不讲框架有多强，只讲**我在这个项目里实际做了什么、踩了什么坑，以及哪些地方代码还没写完**。所有代码都来自仓库里一个能编译、能跑起来的项目（`DeviceGateway`）。

> **先把话说在前面**：文末有一节专门列了「当前代码里还没做对的地方」，包括 **26 个失败的单元测试**。那些坑我没有藏起来 —— 比起给一篇看起来无懈可击的文章，把真实的工程状态写清楚更有用。

> **版本口径**：本文所有代码与结论对应仓库当前状态 —— .NET 10 + SuperSocket **2.1.0**（`SuperSocket.Server` / `SuperSocket.Command` / `SuperSocket.ProtoBase` / `SuperSocket.Client`）。文中所有关于框架行为的判断（`FixedHeaderPipelineFilter` 的假设、`MaxPackageLength` 的比对对象、`LastActiveTime` 的刷新时机、`CommandAttribute.Key` 的类型）都通过反射读取 SuperSocket 2.1.0 程序集核对过，不是凭印象。

---

## 一、项目长什么样

一个 .NET 10 的解决方案，三个项目 + 一个测试项目：

```
DeviceGateway/
├── DeviceGateway.slnx
├── src/
│   ├── DeviceGateway.Shared/     协议契约（服务端与客户端共用同一份实现）
│   │   ├── Protocol/             帧格式、拆包过滤器、编码器
│   │   └── Contracts/            JSON 配置、消息负载 DTO
│   ├── DeviceGateway.Server/     Web API + SuperSocket 服务端
│   │   ├── Commands/             心跳、位置数据命令
│   │   ├── Registry/             设备注册表
│   │   ├── Sessions/             设备会话
│   │   ├── Background/           离线看门狗
│   │   └── Controllers/          HTTP 接口（Controller/Action）
│   └── DeviceGateway.Client/     EasyClient 设备模拟器
└── tests/
    └── DeviceGateway.Tests/      协议拆包与设备注册表单元测试（130 个用例）
```

**为什么单独抽一个 Shared**：帧格式只定义一次。客户端和服务端共用**同一个**过滤器与编码器，避免两边各写一套、逐渐漂移。这是这类项目最容易出问题的地方 —— 客户端按小端写、服务端按大端读，这种 bug 在联调前不会暴露。

### 协议格式

```
0x55 | LEN(4字节大端) | TYPE(1字节) | JSON(n字节) | 0xAA
```

| 字段 | 说明 |
|---|---|
| `0x55` | 帧起始标记 |
| `LEN` | **JSON 负载的字节数 n**（不含类型字节、不含长度域本身、不含帧尾）。4 字节**大端序** |
| `TYPE` | 消息类型：`0x01` 心跳、`0x02` 位置数据、`0x03` 通用上报 |
| `JSON` | UTF-8 编码的 JSON 负载 |
| `0xAA` | 帧结束标记 |

**整帧长度 = `n + 7` 字节**（帧头 6 字节 + 帧尾 1 字节）。这个定义只有一处：

```csharp
// src/DeviceGateway.Shared/Protocol/DeviceFrame.cs
public static class DeviceFrame
{
    public const byte BeginMark = 0x55;
    public const byte EndMark = 0xAA;
    public const int LengthFieldSize = 4;
    public const int MessageTypeFieldSize = 1;
    public const int HeaderSize = 1 + LengthFieldSize + MessageTypeFieldSize;  // = 6

    public static int TotalLength(int jsonPayloadLength) => jsonPayloadLength + HeaderSize + 1;
    public static int EndMarkOffset(int jsonPayloadLength) => HeaderSize + jsonPayloadLength;
}
```

> ⚠️ **这里有个我自己踩过的 off-by-one**：我最初在文档里写的是"整帧长度 = n + 6"，少算了帧尾那 1 个字节。代码里的 `TotalLength` 是 `n + HeaderSize + 1`（`+6 +1`），也就是 **n + 7**。这种错误只写在文档里还不要紧 —— 一旦编码器和解码器各按各的理解写，就变成"设备发 24 字节、服务端等 25 字节"的死锁。**长度语义必须只有一个定义来源，并且代码和文档对得上。**

---

## 二、接线：SuperSocket 和 ASP.NET Core 共用一个宿主

这是整个项目最关键的一段代码，在 `src/DeviceGateway.Server/Program.cs`：

```csharp
var superSocketBuilder = builder.Host.AsSuperSocketHostBuilder<DevicePackage, DeviceFrameFilter>()
    .UseSession<DeviceSession>()
    .UseCommand(options =>
    {
        options.AddCommand<HeartbeatCommand>();
        options.AddCommand<PositionCommand>();

        // 设备发来未支持的消息类型时走这里，而不是让服务端崩掉。
        options.RegisterUnknownPackageHandler<DevicePackage>((session, package, ct) =>
        {
            session.GetDefaultLogger().LogWarning(
                "收到未支持的消息类型 0x{MsgType:X2}，负载：{Payload}，已忽略",
                package.MessageType, package.Payload);
            return ValueTask.CompletedTask;
        });
    })
    .UseInProcSessionContainer();

// ConfigureHostBuilder() 是 IMinimalApiHostBuilder 上的显式接口实现，
// 不能直接链式调用，要先把 builder 转成接口类型。
((IMinimalApiHostBuilder)superSocketBuilder.AsMinimalApiHostBuilder()).ConfigureHostBuilder();
```

拆开看：

**`AsSuperSocketHostBuilder<包类型, 过滤器类型>()`** —— 两个泛型参数分别是消息包和拆包过滤器。这一步就把你的协议"钉"进了框架。

**`.UseSession<DeviceSession>()`** —— 自定义会话类型，承载设备身份绑定、发送锁这些状态。

**`.UseCommand(...)` + `AddCommand<T>()`** —— 命令模式，按消息类型自动路由：

```csharp
[Command("Heartbeat", Key = (byte)DeviceMessageTypes.Heartbeat)]
public class HeartbeatCommand : IAsyncCommand<DeviceSession, DevicePackage>
{
    public async ValueTask ExecuteAsync(
        DeviceSession session, DevicePackage package, CancellationToken cancellationToken)
    {
        // ...
    }
}
```

包类型实现 `IKeyedPackageInfo<byte>`，框架就按 `Key`（帧里的类型字节）找对应命令：

```csharp
public sealed class DevicePackage : IKeyedPackageInfo<byte>
{
    public byte Key { get; set; }              // 消息类型字节
    public byte MessageType => Key;            // 业务语义别名
    public string Payload { get; set; } = "";  // JSON 负载（已是文本，不再二次序列化）
}
```

**`.UseInProcSessionContainer()`** —— 会话容器，用来**按会话查找连接**。跨设备消息需要这个：HTTP 请求来了，得知道目标设备的 TCP 连接在哪。

### 这里有两个真实的坑

**坑 1：命令不会自动发现。** 你把命令类写好了、标好 `[Command]` 了，然后发现运行起来什么都没发生。原因：SuperSocket **不做命令的自动扫描发现**，`[Command]` 特性只是元数据，路由表是你手动往里塞的。忘了 `AddCommand<T>()`，这个命令就永远不会被调用。

**坑 2：`Key` 写成裸的 `0x01` 会启动即崩。** 我用反射确认了 `CommandAttribute` 的定义：

```csharp
public object Key { get; set; }   // ← 注意是 object，不是 byte
```

`Key` 的类型是 `object`。你写 `Key = 0x01`，这个字面量是 **`int`**，装箱成了 `System.Int32`。框架在拆箱时按 `byte` 取，直接抛 `InvalidCastException`，服务启动即崩。正确写法必须显式转换：

```csharp
Key = (byte)DeviceMessageTypes.Heartbeat    // ✅
Key = 0x01                                  // ❌ 装箱成 int，启动崩
```

**这类"能编译、能启动前跑、启动就崩"的 bug 最耗时间。** 因为你会怀疑是不是配置写错了、包类型写错了 —— 实际上是 3 个字符的强制转换。

### 共用宿主的价值

SuperSocket 的 TCP 监听和 ASP.NET Core 的 HTTP 监听跑在**同一个 `IHost`** 里：同一个 DI 容器、同一套配置、同一份日志、一个进程一个生命周期。设备接入服务要同时提供 TCP 和 HTTP 接口时，这一点很值。

---

## 三、拆包：这是整个项目技术含量最高的部分

### 先看内置 filter 为什么不够用

**`FixedHeaderPipelineFilter<T>`** —— 它实际的行为是：基类的 `Filter` 内部用 `reader.Sequence.Slice(0, headerSize)` 取帧头，也就是**假定帧头就在序列的第 0 字节**，没有"扫描下一个 `0x55`"的能力。

我写了个最小复现，喂给它一个带脏字节前缀的缓冲区：

```
--- clean frame, base class ---
    [GetBodyLengthFromHeader] buffer.Length=6
    -> payload len=3
    [DecodePackage] buffer.Length=10
  result=decoded consumed=10

--- garbage prefix (0x01 0x02) then frame, base class ---
    [GetBodyLengthFromHeader] buffer.Length=6
    -> payload len=39124992      ← 把脏字节当成长度域读了
  result=null consumed=0          ← 消费 0 字节，等更多数据
```

第二段就是**流就此卡死**：基类把 `0x55` 前面那两个字节当成长度域，解析出一个 39124992 的荒谬长度，然后返回"消费 0 字节"。框架于是永远等更多数据。表现就是某台设备莫名其妙不再上报了，服务端日志一片安静，连个异常都没有。

**`BeginEndMarkPipelineFilter<T>`** —— 按**第一个 `0xAA`** 截断。问题有两个：它**完全忽略长度字段**，只看头尾标记；而 UTF-8 里的 `0xAA` 并不罕见，JSON 字符串里一个字符就可能踩中。找不到帧头时它还会抛 `ProtocolException`，而 SuperSocket 对 filter 抛异常的处理是**直接以 ProtocolError 断连** —— 在 filter 里抛异常 = 断连，不是容错。

### 所以结论是：自定义二进制协议，基本都得自己写 filter

**我的实现思路有四条**：

**原则一：帧边界由长度域确定，帧尾只做校验、不参与查找。**

```csharp
// 按长度定位到固定偏移比对，而不是搜索 0xAA
var endMarkOffset = markerOffset + DeviceFrame.HeaderSize + (int)payloadLength;
var endMark = ReadByteAt(reader.Sequence, endMarkOffset);
if (endMark != DeviceFrame.EndMark)
{
    // 长度域与实际内容不符 —— 这个 0x55 是脏数据，跳过它继续找
    SkipMarker(ref reader);
    continue;
}
```

这样 JSON 里含 `0xAA` 也**不会截断**。因为我们从来不"搜索" `0xAA`，只是"按长度跳到固定位置看一眼"。

**原则二：脏数据只重同步、绝不抛异常。**

```csharp
while (true)
{
    var markerOffset = FindBeginMark(ref reader);   // 找下一个 0x55
    if (markerOffset < 0) return null;               // 全是脏字节，丢掉

    // 长度域不可信 → 这个 0x55 是脏数据 → 跳过它，继续找下一个 0x55
    if (payloadLength > MaxPayloadLength)
    {
        SkipMarker(ref reader);
        continue;
    }
    ...
}
```

**"跳过它继续找下一个"就是重同步（resync）。** 一个坏标记不会毁掉整条流。

这里有个细节很多人会写错：**数据不足时必须"退回起点，一个字节都不消费"**，而**脏字节必须消费掉**。因为脏字节如果留在缓冲区里，解析永远无法前进。

> 这两者的区别是：**"数据不足"要回退（数据有用，留着等），"脏数据"要消费（数据没用，扔掉）。** 分不清这两者，filter 要么卡死要么丢包。

**原则三：长度域必须自校验上限。**

```csharp
public const int MaxPayloadLength = 64 * 1024;

if (payloadLength > MaxPayloadLength)
{
    // 长度域是外部输入！伪造的巨大长度会导致巨额内存分配
    SkipMarker(ref reader);
    continue;
}
```

这一条很多人会漏，而且漏得有道理 —— 因为**框架的 `MaxPackageLength` 检查根本帮不上忙**。我用反射追了调用点，它在 `PipeConnectionBase.ReadBuffer` 里，比的是**从 socket 读进缓冲区的字节数**，不是**你在负载里声明的长度**。我实测过：声明一个 100MB 的长度，基类返回"消费 0 字节"，检查压根没触发。

> **长度域是攻击面** —— 它是外部输入，必须自己设上限。

**原则四：无状态。** 每次调用都从 reader 当前位置重新解析。因为框架的行为是"解出整帧后调 `Reset()`、半帧时不调"，**无状态实现天然免疫这个差异**，你不需要去猜框架什么时候会调 `Reset()`。

### 顺带一提：TCP 缓冲区不是连续的

```csharp
// ⚠️ 不能这么写
var b = reader.Sequence.FirstSpan()[offset];   // 只在单段时成立！

// ✅ 用 SequenceReader 逐段推进
private static byte ReadByteAt(ReadOnlySequence<byte> sequence, long offset)
{
    var reader = new SequenceReader<byte>(sequence);
    reader.Advance(offset);
    reader.TryPeek(out var value);
    return value;
}
```

TCP 缓冲区的 `ReadOnlySequence<byte>` 可能是**多段的**（非连续内存）。`FirstSpan()` 只在单段时成立，**一旦出现多段就是越界访问**。这个 bug 在低负载时可能一直不复现，上线后偶发一次。

> **filter 层的三个必答题**：
> 1. 帧边界靠什么确定？（长度域 / 定长 / 分隔符）
> 2. 脏数据怎么处理？（重同步 / 断连）
> 3. 长度上限是多少？

---

## 四、离线判定：`UseClearIdleSession()` 判不了"设备离线"

这个坑的隐蔽程度排第一。

SuperSocket 内置了 `UseClearIdleSession()`，看起来正好解决"设备长时间不活跃就清理"。但我用反射追了它的实现（`ClearIdleSessionMiddleware`，XML 注释写着 "Middleware for clearing idle sessions based on their last active time"），并追了 `LastActiveTime` 的更新点：

```
SuperSocket.Connection :: PipeConnection.<FillInputPipeAsync>d__13 calls UpdateLastActiveTime
SuperSocket.Connection :: PipeConnection.<ProcessOutputRead>d__14  calls UpdateLastActiveTime
```

**读和写都会刷新它。** 而 `LastActiveTime` 表达的是"**连接有 I/O**"，不是"**设备还活着**"。

在我们的场景里：设备**每秒**发一包位置数据。于是：

```
LastActiveTime 永远是 1 秒前
⇒ 那个 600 秒的超时永远不触发
⇒ 心跳已停但连接还挂着的设备会永远不被清理
```

**用了这个机制 = 以为有在线检测，实际没有。** 而且它不报错、不告警，你只会在生产环境看到"离线设备的连接一直堆积"。

### 正确做法：业务层心跳看门狗

```csharp
public class DeviceHeartbeatMonitor : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeout = TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds);   // 90 秒
        var interval = TimeSpan.FromSeconds(_options.OfflineScanIntervalSeconds); // 10 秒

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var expiredSessions = _registry.ExpireHeartbeatSessions(DateTimeOffset.UtcNow, timeout);
            foreach (var session in expiredSessions)
            {
                await session.CloseAsync();
            }
        }
    }
}
```

配置在 `appsettings.json`：

```jsonc
"serverOptions": {
  "name": "DeviceGateway",
  "listeners": [ { "ip": "any", "port": 5001, "backLog": 100, "noDelay": true } ],
  "idleSessionTimeOut": 600,
  "clearIdleSessionInterval": 120
},
"deviceGateway": {
  "heartbeatTimeoutSeconds": 90,
  "offlineScanIntervalSeconds": 10
}
```

> 注意配置的**大小写**：`idleSessionTimeOut` 的 `Out` 是**大写 O**，写成 `idleSessionTimeout` 就绑不上了。这类配置项拼错不会报错 —— 只会静默地使用默认值。

**离线判定的实际延迟是 90～100 秒**（90 秒超时 + 最多 10 秒扫描间隔）。

### 配套的关键设计：位置数据**不应该**刷新心跳时间

这是整个项目里最反直觉的一个决定，但它决定了离线判定准不准。

如果位置数据也刷新心跳时间，那么：

```
设备心跳停了，但还在发位置数据
⇒ 心跳时间被位置数据不断刷新
⇒ 永远判不出离线
```

**高频数据会掩盖心跳缺失。** 这是一个极其容易犯的错误 —— 直觉上"收到数据说明设备活着"，但恰恰相反：**要判活，就必须是那个"专用于判活"的数据。**

同理，位置数据**也不能把已判离线的设备改回在线**，否则看门狗每轮扫描把它标记离线、紧接着又被业务代码改回在线，**状态反复横跳**，日志刷屏且语义混乱。

> **在线状态由两件事决定，且只有这两件：**
> - 心跳是否超时（看门狗负责）
> - 连接是否还在（会话负责）
>
> 位置数据两件都不改变。
>
> `UseClearIdleSession` 只作为**回收僵死 socket 的另一层兜底**。

**这个设计最终落在代码里，是三个方法各管一件事：**

```csharp
// DeviceRegistry.Bind —— 只回答"这条连接属于哪台设备"
// 明确不碰 Status，也不碰 LastHeartbeatAt
_devices[deviceId] = record with { ConnectedAt = now };

// DeviceRegistry.TouchHeartbeat —— 只有心跳命令会调它
// 只有当前绑定的 Session 才允许刷新，防止旧连接干扰新连接
_devices[deviceId] = record with { LastHeartbeatAt = lastHeartbeatAt, Status = DeviceStatus.Online };

// DeviceRegistry.IsCurrentSession —— 位置命令用它识别"旧连接"
// 不刷新心跳、不改在线状态
```

于是 `PositionCommand` 里是这么写的：

```csharp
// 位置数据不刷新心跳时间、不改变在线状态 —— 离线判定只认心跳。
// 这里只做"是不是当前绑定"的校验：设备重连后，旧连接上还在跑的
// 数据处理任务不应该继续产生业务效果。
if (!_registry.IsCurrentSession(deviceId, session))
{
    session.LogWarning("设备位置数据来自旧连接，已忽略，DeviceId: {DeviceId}", deviceId);
    return;
}
```

> **换句话说：只有 `0x01` 心跳能让设备变成在线。**
> 只发位置数据、从不发心跳的设备在接口里一直显示离线 —— 这是刻意的，心跳才是"设备还活着"的唯一信号。

---

## 五、设备身份：只绑定一次，之后只校验

`deviceId` 在 JSON 负载里。看起来最简单的做法是每帧都读一遍 `deviceId`，随便用。

**问题是安全的。**

如果每帧都信任 JSON 里的 `deviceId`，那么任何一个客户端只要在自己的 JSON 里写上 `DRONE-002`，就能**劫持 DRONE-002 的会话绑定** —— 网关会以为它是 DRONE-002，把发给 DRONE-002 的消息送到攻击者手里。

所以规则是：

> **第一帧到达时绑定到会话上，之后每帧只做一致性校验，不一致就丢弃。**

```csharp
public bool TryBindDevice(string deviceId)
{
    if (string.IsNullOrWhiteSpace(deviceId)) return false;

    lock (_bindLock)
    {
        if (IsClosing) return false;

        // 第一次绑定
        if (string.IsNullOrEmpty(_deviceId))
        {
            Volatile.Write(ref _deviceId, deviceId);
            return true;
        }

        // 后续只能使用相同的设备编号
        return string.Equals(_deviceId, deviceId, StringComparison.Ordinal);
    }
}
```

一次性解决了两个问题：

- **安全** —— 任何客户端都不能在 JSON 里写别人的 `deviceId` 来劫持会话绑定
- **性能** —— 绑定之后不用每帧都解析 JSON 取 `deviceId`（设备每秒一包，能省则省）

校验不通过时，**丢弃这一帧，不要断开连接**：

```csharp
session.LogWarning(
    "会话 {SessionId} 声称的设备标识 {Claimed} 与已绑定的 {Bound} 不一致，已丢弃",
    session.SessionID, deviceId, session.DeviceId);
```

读 `deviceId` 的地方也要对设备数据保持不信任：

```csharp
public static bool TryReadDeviceId(DevicePackage package, out string deviceId)
{
    deviceId = string.Empty;
    if (string.IsNullOrWhiteSpace(package.Payload)) return false;

    try
    {
        using var doc = JsonDocument.Parse(package.Payload);
        if (!doc.RootElement.TryGetProperty("deviceId", out var element)) return false;
        if (element.ValueKind != JsonValueKind.String) return false;

        var value = element.GetString();
        // deviceId 是设备的唯一标识，空值或超长都视为非法。
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return false;

        deviceId = value;
        return true;
    }
    catch (JsonException)
    {
        // 负载不是合法 JSON —— 当作坏消息处理，不抛异常。
        return false;
    }
}
```

**一条坏消息不能让整条连接断掉** —— 这是设备接入场景的基本要求。

---

## 六、并发写同一个会话，字节会交错

这是**生产环境才偶发**的那种 bug，而且极难复现。

场景是这样的：

- 设备上报位置数据 → 服务端在命令处理器里回复一条响应
- 同时，HTTP 接口触发了一条跨设备消息 → 服务端往**同一个会话**写

两条路径同时往一个 socket 写。TCP 的写操作如果并发进行，**字节会交错**，发出去的是一帧拼接了另一帧半截的垃圾数据。

设备侧解析失败 → 断线 → 重连 → 又是垃圾数据。经典的"鬼故事"。

### 解法：单一写者（Single Writer）

每个会话有一个发送锁 + **唯一一条写路径**，所有发送都排队走它：

```csharp
private readonly SemaphoreSlim _sendLock = new(1, 1);

public async Task SendPackageAsync(
    DevicePackage package,
    CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(package);

    ThrowIfClosing();                    // 第一次检查：尽早拒绝已关闭的会话

    await _sendLock.WaitAsync(cancellationToken);
    try
    {
        ThrowIfClosing();                // 第二次检查：避免等待锁期间连接已关

        await ((IAppSession)this).SendAsync(_encoder, package, cancellationToken);
    }
    finally
    {
        _sendLock.Release();
    }
}
```

> `((IAppSession)this)` 这个写法有点丑，但 `SendAsync(encoder, package)` 是 `IAppSession` 的接口方法，显式转接口是稳妥做法。

**双重检查（double check）不是多余的**：第一次检查在锁外，快速失败；第二次检查在锁内，避免"等锁期间连接已经关了"。

### 所有出口必须收敛到同一个方法

这是我在实现过程中**专门加了一条测试**才守住的设计。`DeviceSession` 现在有三个看起来都能"发送"的方法：

| 方法 | 作用 | 是否走发送锁 |
|---|---|---|
| `SendPackageAsync` | 唯一的真实发送出口 | ✅ |
| `SendSerializedAsync` | `SendPackageAsync` 的可测试形态（持锁 + 注入 transport） | ✅ |
| `ISessionHandle.SendAsync` | 注册表用的接口实现 | ✅ 转发到 `SendPackageAsync` |

**三个全部走同一把锁。** 我特意把 `ISessionHandle.SendAsync` 写成转发而不是独立实现：

```csharp
public async Task SendAsync(DevicePackage package, CancellationToken cancellationToken)
{
    await SendPackageAsync(package, cancellationToken);
}
```

如果有人图省事，直接在 `ISessionHandle.SendAsync` 里调 `((IAppSession)this).SendAsync(...)`，那就把发送锁绕过去了 —— 并发写立刻回来。**"所有出口收敛到同一个方法"不是风格问题，是并发正确性的前提。**

### 为什么用 `SemaphoreSlim` 而不是 `Channel`

我最初的设计是**有界 `Channel` + 唯一写循环**，队列满时丢弃最旧的，慢设备就不会把 HTTP 请求拖住。但最终落地的是 `SemaphoreSlim`：

- **优点**：保证严格串行，语义更简单，出问题好排查
- **代价**：慢设备会阻塞发送方 —— HTTP 请求会一直等

**如果你的场景里发送方不能被慢设备阻塞，就得换回 `Channel`。** 这是个明确的取舍，不是"哪个更好"的问题。

### 怎么测"并发写不会交错"

这是我觉得最值得分享的一个测试技巧。直接测 socket 很难 —— 你没法稳定地制造竞态。做法是**把"持锁"和"怎么发"分开**：

```csharp
internal async Task SendSerializedAsync(
    DevicePackage package,
    Func<DevicePackage, CancellationToken, Task> transport,
    CancellationToken cancellationToken)
```

生产环境传入真实的 socket 发送；单元测试传入一个记录用的假实现，然后**并发发起多次发送，检查假实现收到的包是否始终完整、顺序是否串行**：

```csharp
// 伪代码
var received = new List<DevicePackage>();
await Task.WhenAll(Enumerable.Range(0, 100).Select(i =>
    session.SendSerializedAsync(package, (p, ct) => { received.Add(p); return Task.CompletedTask; }, default)));

// 断言：received 里没有交错、没有丢失
```

**锁的逻辑因此可以被确定性地验证**，而不是靠压测碰运气。

---

## 七、重连时，旧会话的关闭事件会误删新会话

这个 bug 的表现是：**"设备明明在线，但网关查不到它。"**

原因是这样的竞态：

```
时刻 T1：设备 A 的旧连接还在
时刻 T2：设备 A 重连，新会话 S2 建立，绑定成功
时刻 T3：旧会话 S1 此时才触发关闭事件
时刻 T4：S1 的关闭处理把 deviceId → S2 的绑定删掉了 ❌
```

结果是：设备明明在线，注册表里却查不到它的会话，跨设备消息发不出去。

### 解法：比较删除（Compare-and-Delete）

注销时**必须确认"当前绑定的确实是自己"**，才能删：

```csharp
public bool UnbindAndMarkOffline(string deviceId, ISessionHandle session)
{
    lock (_gate)
    {
        if (!_sessions.TryGetValue(deviceId, out var current)) return false;

        // 关键：只有当前绑定的 Session 确实是自己时才删
        if (!ReferenceEquals(current, session)) return false;

        _sessions.Remove(deviceId);
        _devices[deviceId] = record with { Status = DeviceStatus.Offline };
        return true;
    }
}
```

`ReferenceEquals` 这一行，就是拦住那个竞态的关键。

### 同时：重连时主动踢掉旧连接

否则一个设备占着两条连接，推消息给哪条是不确定的：

```csharp
var staleSession = Bind(deviceId, session, now);
if (staleSession != null)
{
    await staleSession.CloseAsync();   // 锁外关闭
}
```

> **注意**：`CloseAsync()` 会触发关闭事件，进而回调 `UnbindAndMarkOffline`。因为有了上面的"比较删除"，这次回调会发现自己已经不是当前绑定，**安全地跳过**。两个机制是配套的。
>
> **另一个细节**：关闭旧连接必须在**注册表锁外**做。关闭可能要等网络操作，持着锁等会把整个注册表堵住。

### 关于线程安全策略

注册表目前用的是**一把大锁 `_gate`** 保护全部状态（两个 `Dictionary`）。这当然正确 —— 而且"状态转换的一致性"本来就比"并发读性能"重要。

但要说清楚一点：**不是所有方法都必须在同一把锁下。** `ExpireHeartbeatSessions` 就是分两段的：

```csharp
// 第一步：锁内取快照
lock (_gate) { snapshot = _devices.Values.ToArray(); }

// 第二步：锁外筛选（纯计算，不碰共享状态）
var candidates = snapshot.Where(x => x.Status == DeviceStatus.Online &&
                                      now - x.LastHeartbeatAt > timeout).ToArray();

// 第三步：逐个设备重新进锁做最终确认
foreach (var candidate in candidates)
{
    lock (_gate)
    {
        // 重新读取最新状态 —— 期间可能已经变了
        if (current.Status != DeviceStatus.Online) continue;
        if (now - current.LastHeartbeatAt <= timeout) continue;
        ...
    }
}
```

**"锁内取快照 → 锁外计算 → 锁内复核"** 是处理这类"扫描 + 条件更新"的标准姿势。锁外那段不会持有任何共享状态的引用，所以安全。

---

## 八、中文 JSON 被转义成 `\uXXXX`

这个坑的表现很隐蔽：HTTP 接口里传的中文正常，但**设备收到的负载变成了 `"中文"`**。

原因在 ASP.NET Core 的默认 JSON 配置：`.NET 9+` 的默认值是 `JavaScriptEncoder.Default`，它会把所有非 ASCII 字符转义成 `\uXXXX`。而我们的流程是：

```
HTTP 请求 → ASP.NET Core 序列化 → 放进 DevicePackage.Payload → 原样转发给设备
```

**中间没有任何一层做反转义**，设备收到的就是转义后的文本。

修复：

```csharp
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder =
        System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    options.SerializerOptions.PropertyNamingPolicy =
        System.Text.Json.JsonNamingPolicy.CamelCase;
});
```

而且**客户端和服务端必须用同一份配置**，否则两边序列化出来的不一样：

```csharp
public static class DeviceGatewayJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static bool TryDeserialize<T>(string json, out T? value)
    {
        // 设备数据不可信，一条坏消息不能让连接崩掉 —— 捕获异常，返回 false
    }
}
```

> **推论**：凡是把"用户输入的文本"原样透传到另一个系统的链路，**每一层的序列化配置都必须显式统一**。这类 bug 在纯 ASCII 测试下永远测不出来。

---

## 九、完整链路串一遍

```
┌──────────────── 设备 ────────────────┐
│  DeviceSimulator                      │
│    每 10s: 心跳 0x01（真实设备 30s）   │
│    每  1s: 位置 0x02                  │
└──────────────┬────────────────────────┘
               │ TCP
               ▼
┌───────────────────────────────────────────────┐
│ DeviceFrameFilter        ← 自己实现，认长度域   │
│   拆包 / 粘包 / 半包 / 脏字节重同步             │
└──────────────┬────────────────────────────────┘
               ▼
┌───────────────────────────────────────────────┐
│ DevicePackage : IKeyedPackageInfo<byte>        │
│   Key = 类型字节, Payload = JSON               │
└──────────────┬────────────────────────────────┘
               ▼
┌───────────────────────────────────────────────┐
│ 命令路由（按 Key）                              │
│   0x01 → HeartbeatCommand                      │
│   0x02 → PositionCommand                       │
│   其他 → UnknownPackageHandler（记日志，不崩）   │
└──────────────┬────────────────────────────────┘
               ▼
┌───────────────────────────────────────────────┐
│ DeviceRegistry        ← 设备档案 + 在线会话      │
│   TryBind → 首次绑定 / 之后校验                 │
│   Bind → 重连踢旧连接（不改在线状态）           │
│   UnbindAndMarkOffline → 比较删除              │
│   TouchHeartbeat → 刷新心跳 + 置在线            │
│   IsCurrentSession → 位置数据识别旧连接         │
└──────────────┬────────────────────────────────┘
               ▼
┌───────────────────────────────────────────────┐
│ DeviceHeartbeatMonitor  ← 业务层离线看门狗       │
│   90 秒没心跳 → 标离线 + 关连接                 │
└───────────────────────────────────────────────┘

HTTP 侧（同一个进程、同一个 DI 容器）：
  GET  /api/Device/List
  GET  /api/Device/Detail?deviceId=xxx
  POST /api/Device/SendMessage     → 走会话发送锁
  GET  /api/Health/Check
```

### 客户端：`EasyClient` + `.AsClient()`

```csharp
// ConnectAsync 在底层是 protected，必须通过 AsClient() 拿到接口来调用
var client = new EasyClient<DevicePackage, DevicePackage>(
    new DeviceFrameFilter(),
    _encoder).AsClient();

client.PackageHandler += (_, package) =>
{
    Console.WriteLine($"[{_deviceId}] 收到服务端消息 类型=0x{package.MessageType:X2} 内容={package.Payload}");
    return ValueTask.CompletedTask;
};

client.Closed += (_, _) => Console.WriteLine($"[{_deviceId}] 连接已断开");

var connected = await client.ConnectAsync(_endpoint, _cts.Token);
client.StartReceive();     // ← 别忘了启动接收循环
```

> ⚠️ **`StartReceive()` 必须显式调用。** `ConnectAsync` 只负责连接，接收循环要自己启动。忘了这行，客户端连上了但永远收不到服务端的下行消息。

**模拟器的两个工程细节**：

- **重连要退避**：从 1 秒开始，每次翻倍，上限 30 秒。没有退避的无限重连，遇到服务端重启时会打成一片。
- **连上先发一次心跳**：让服务端尽快登记设备，不用干等第一个心跳周期。

---

## 十、跑起来看看

### 启动服务端

```bash
dotnet run --project src/DeviceGateway.Server
```

- 设备接入 TCP 端口：`5001`（配置在 `appsettings.json` 的 `serverOptions.listeners`）
- HTTP 接口端口：`launchSettings.json` 里是 **5223**（http）/ **7022**（https）

> 注意 HTTP 端口和 TCP 端口是**两套完全独立的配置**：TCP 在 `serverOptions.listeners`，HTTP 在 ASP.NET Core 自己的 `applicationUrl`。想固定用 8080 就显式指定：

```bash
dotnet run --project src/DeviceGateway.Server --urls "http://localhost:8080"
```

### 启动模拟设备

```bash
dotnet run --project src/DeviceGateway.Client -- --count 2 --host 127.0.0.1 --port 5001
```

参数：`--count` `--host` `--port` `--prefix` `--heartbeat` `--position`

| 参数 | 默认值 | 说明 |
|---|---|---|
| `--count` | 1 | 模拟几台设备 |
| `--host` | 127.0.0.1 | 服务端地址 |
| `--port` | 5001 | 服务端端口 |
| `--prefix` | DRONE | 设备标识前缀（实际标识形如 `DRONE-001`） |
| `--heartbeat` | 10 | 心跳间隔（秒）。真实设备常用 30 秒，这里取折中值便于演示 |
| `--position` | 1 | 位置上报间隔（秒） |

### 调用接口

```bash
# 设备列表（含在线状态、最后心跳时间）
curl http://localhost:8080/api/Device/List

# 单个设备详情
curl "http://localhost:8080/api/Device/Detail?deviceId=DRONE-001"

# 跨设备消息：给 DRONE-002 发一条
curl -X POST http://localhost:8080/api/Device/SendMessage -H "Content-Type: application/json" \
  -d '{"from":"DRONE-001","to":"DRONE-002","payload":{"msg":"hello"}}'

# 健康检查
curl http://localhost:8080/api/Health/Check
```

### 接口返回码

| 场景 | 返回码 | 含义 |
|---|---|---|
| 发送成功 | 200 | 已发往目标设备（响应体 `status: "sent"`） |
| 目标设备不存在 | 404 | 设备从没上报过，网关不认识它（`status: "unknown"`） |
| 目标设备离线 | 409 | 设备存在但当前不在线（`status: "offline"`） |
| 参数非法（空、给自己发） | 400 | — |
| 发送过程抛异常 | 502 | — |

### 手工验证分片与脏数据

用裸 TCP 客户端精确控制字节到达的时机：

```python
import socket, struct, json, time

def frame(t, payload):
    f = bytearray()
    f.append(0x55); f += struct.pack(">I", len(payload)); f.append(t)
    f += payload; f.append(0xAA)
    return bytes(f)

s = socket.create_connection(("127.0.0.1", 5001))
f = frame(0x01, json.dumps({"deviceId": "SPLIT-001"}).encode())

s.sendall(f[:len(f)//2])      # 前半
time.sleep(2.0)
s.sendall(f[len(f)//2:])      # 后半
time.sleep(1.0)
```

然后 `curl http://localhost:8080/api/Device/List` 确认 `SPLIT-001` 已登记。
在前面加几个垃圾字节（如 `b"\xde\xad\xbe\xef"`）再试一次 —— 设备仍应被正确登记，
服务端日志里也不应出现 `Protocol error`。

---

## 十一、部署时的两个坑

### 坑：容器内监听地址必须是 `any`

```jsonc
"serverOptions": {
  "listeners": [
    { "ip": "any", "port": 5001, "backLog": 100, "noDelay": true }
  ]
}
```

**写 `127.0.0.1` 会导致容器外连不上** —— 这是容器化最常见的坑。容器里的 `127.0.0.1` 是容器自己，不是宿主机。

配置外置用 ASP.NET Core 的双下划线层级约定：

```bash
docker run -p 8080:8080 -p 5001:5001 \
  -e serverOptions__listeners__0__port=5001 \
  -e serverOptions__listeners__0__ip=any \
  -e deviceGateway__heartbeatTimeoutSeconds=90 \
  device-gateway:dev
```

Dockerfile 是多阶段构建，用非 root 用户运行，并且带健康检查：

```dockerfile
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD ["/bin/sh", "-c", "curl -fsS http://127.0.0.1:8080/api/Health/Check || exit 1"]
```

### 坑：日志刷屏

```jsonc
"Logging": {
  "LogLevel": {
    "SuperSocketService": "Debug"     // ← 注意是这个名字
  }
}
```

注意类别名是 **`SuperSocketService`** —— **不是你的命令类名**。因为 `session.Logger` 是框架内部创建的 logger，类别是框架的。

设备每秒上报一次位置，用 `Information` 级别会刷屏。**生产环境保持默认的 `Information`，`Debug` 只在开发时开启。**

> 顺便一提：我一开始把这个日志写成了 `Debug`，结果发现"日志根本不打印"—— 因为默认配置下 `Debug` 是关闭的。**开发期要看的日志，就别写在默认不开的级别上。**

---

## 十二、多厂家扩展的思路（本期未实现）

架构目标是"接入多个厂家、多种协议"。SuperSocket v2 里这叫**多 Server**，用 `AsSuperSocketApplicationBuilder`：

```csharp
builder.AsSuperSocketApplicationBuilder(hostBuilder => hostBuilder
  .AddServer<VendorASocketService, VendorAPackage, VendorAFilter>(server =>
  {
      server.ConfigureServerOptions((ctx, config) => config.GetSection("VendorA"))
            .UseCommand(commandOptions => commandOptions.AddCommandAssembly(typeof(VendorACommand).Assembly));
  }));

var app = builder.Build();
app.AdaptMultipleServerHost();   // ← 必须调用
```

**每个厂家 = 一个独立的包类型 + 一个独立的过滤器 + 一个独立的监听端口。**

两个坑（来自 `ssdemo01` 的实践，**本项目尚未在多 Server 下验证**）：

1. **配置节里不要写 `assemblies`**。多 Server 时每个 Server 都会绑定这个配置节，而 `CommandOptions.GetCommandTypes` 会**修改** `CommandSources` 列表 ⇒ 命令重复注册 ⇒ 启动抛 `Duplicated command with Key ...` 异常。改用代码里 `AddCommandAssembly(...)`。
2. **多 Server 各有独立的 DI 容器**。普通 singleton 会变成**每个 Server 一个实例**，跨厂家互发消息会失败。要共享必须**注册同一个实例**，并用 `ReferenceEquals` 断言验证。

---

## 十三、诚实地说：这个方案还不能横向扩展

**"Docker 部署"和"多副本横向扩展"是两件事。** 本项目完成的是前者：

| 项目 | 状态 |
|---|---|
| 容器化（Dockerfile、健康检查、配置外置） | ✅ 已完成 |
| 单副本运行 | ✅ 可用 |
| 多副本横向扩展 | ⚠️ **当前不支持** |

**为什么？** 设备注册表是**进程内内存**状态：

```
设备连到副本 A → 副本 A 的注册表里有这个会话
HTTP 请求打到副本 B → 副本 B 查不到该设备 → 跨设备消息发不出去
```

### 两条演进路径

**路径一：一致性哈希 + 转发（推荐）**
前端读第一帧拿到 `deviceId`，按 `hash(deviceId)` 找到归属副本，把 TCP 流转发过去。这样"一个设备的会话永远在同一个副本"，HTTP 请求也按同样的哈希路由。

**路径二：共享注册表 + 发布订阅**
各副本把 `deviceId → 副本` 记在 Redis 里，HTTP 请求经 Redis Pub/Sub 转发到持有该会话的副本。

> ⚠️ **不要用源 IP 做粘滞** —— NAT 后的设备共用 IP，重连还会换源端口。

---

## 十四、当前代码里还没做对的地方

这一节是这篇文章里我最想写清楚的。**上面的方案有一部分还没在代码里真正落地**，我把它们如实列出来。

### 1. 26 个单元测试是失败的

```
失败!  - 失败: 26，通过: 104，已跳过: 0，总计: 130
```

**全部 26 个失败都集中在一个根因**：当前的 `DeviceFrameFilter` 继承 `FixedHeaderPipelineFilter<DevicePackage>`，而**它抛异常**。

失败分布：

| 测试类 | 失败 | 通过 | 总数 |
|---|---|---|---|
| `DeviceFrameFilterTests` | 8 | 15 | 23 |
| `DeviceFrameValidationTests` | 8 | 5 | 13 |
| `DeviceFrameFragmentationTests` | 5 | 4 | 9 |
| `DeviceFrameFrameworkContractTests` | 5 | 6 | 11 |
| 其余 6 个测试类（编码器/注册表/会话/命令） | 0 | 104 | 104 |

**失败堆栈长这样**：

```
SuperSocket.ProtoBase.ProtocolException : 非法帧头
   at DeviceGateway.Shared.Protocol.DeviceFrameFilter.GetBodyLengthFromHeader(...)
      in ...\DeviceFrameFilter.cs:line 61
   at SuperSocket.ProtoBase.FixedHeaderPipelineFilter`1.Filter(SequenceReader`1& reader)
```

**这意味着什么？** 回顾第三节的原则二 —— **filter 抛异常 = 断连，不是容错**。SuperSocket 2.1.0 的 `PipeConnectionBase.ReadBuffer` 收到 filter 抛出的 `ProtocolException`，会以 `CloseReason.ProtocolError` **关闭连接**。

所以现在的真实行为是：

> **链路上只要出现一个脏字节，这条设备连接就被直接断开。**

而设计要求是"脏字节只重同步，不断连"。这两件事直接冲突。

**正确的实现其实写好了** —— 就在同一个文件下方，从第 186 行开始是一大段被注释掉的 `IPipelineFilter<DevicePackage>` 实现（约 220 行）：找帧头、跳过脏字节、按长度定位校验帧尾、无状态、绝不抛异常。**那段代码就是第三节讲的四条原则的落地。**

它没生效的原因很朴素：类名撞了。那段实现也叫 `DeviceFrameFilter`，所以在重命名时整块被注释掉了。**正确的做法是把这套实现启用、删掉现在这个继承基类的版本** —— 因为继承 `FixedHeaderPipelineFilter` 就得把 `Filter` 整个覆盖掉，那时基类只剩三个自动属性可用。

> 这也是我想在文章里强调的一点：**测试是唯一能告诉你"你以为你写好了"的东西。** 我在这个项目里一度以为 filter 写好了 —— 因为注释写得很完整、逻辑读起来很对。但 26 个红灯摆在那儿。

**关于测试本身的两点说明**：

- 测试里那个 `FrameDecoder` 辅助类是**逐行对照** `PipeConnectionBase.ReadBuffer` 的语义写的，严格遵守三条契约：每轮循环新建 `SequenceReader`、解出包后才调 `Reset()`、返回 null 时按消费量决定去留。**所以这些用例同时也在验证"过滤器满足框架调用契约"**，不是自说自话的单元测试。
- 有一类用例特别值得注意：`Framework_FilterNeverThrows_AcrossGarbageCorpus` 会喂 **2000 个随机缓冲区**（固定种子 `20261101`）断言"永不抛异常"。**随机语料 + 铁律断言**，这是测 filter 最省心的方式 —— 你不用穷举自己想得到的坏输入。

### 2. `PositionCommand` 的心跳处理 —— 已经改对了

这一条曾经是错的（当时 `PositionCommand` 里调的是 `TouchHeartbeat`，和高频数据不刷新心跳的规则矛盾）。**现在已经改成了 `IsCurrentSession`** —— 只校验"是不是当前绑定"，不碰心跳时间、不碰在线状态。

对应地，测试 `PositionCommandTests`（12 个用例）全绿，锁的就是这条需求。

**但这也说明了一件事**：这类"设计意图 vs 代码实现"的偏差，**光看代码是发现不了的** —— 代码读起来完全合理。是测试把它钉住的。

### 3. README 和代码有出入

文档和实现不一致的地方，**比 bug 更难发现**，因为读哪边都"说得通"：

| 文档写的 | 代码实际 |
|---|---|
| 会话用"有界 `Channel` + 唯一写循环" | 用的是 `SemaphoreSlim`（源码里**根本没有 `Channel`**） |
| 比较删除用 `ConcurrentDictionary` 的 `ICollection<KeyValuePair<>>.Remove` | 用的是 `lock (_gate)` + `ReferenceEquals` |
| 整帧长度 = `n + 6` | `TotalLength` 是 `n + 7` |
| `DeviceFrameFilter` 直接实现 `IPipelineFilter` | 当前继承 `FixedHeaderPipelineFilter`（重构中留下的不一致） |

**文档会骗人，代码不会。** 但最好的结果是两边对齐 —— 尤其是 `Channel` vs `SemaphoreSlim` 这种会影响并发语义的分歧。

### 4. `DeviceRecord` 的 `Vendor` 字段目前是写死的

`DeviceRecord.Vendor` 的默认值是 `"vendorA"`，注册表里没有任何地方根据连接来源给它赋值 —— 现在是给"多厂家"预留的字段，还没接上。等真正做多 Server 时，得由每个 Server 在登记设备时写入自己的厂家标识。

### 5. 没有端到端集成测试

现在的测试都是单元测试。**"设备连上来、发心跳、HTTP 下发消息、设备收到"这条完整链路，没有任何自动化测试覆盖。** 上面那些 filter 的问题，如果有一个端到端测试，第一天就能发现。

README 里那段 Python 手工验证脚本就是权宜之计 —— **它验证了，但它不会在 CI 里跑**。

---

## 十五、一份 checklist

如果你也要用 SuperSocket v2 做设备接入，这张表可以直接抄：

**协议层**
- [ ] 帧边界靠什么确定？（长度域 / 定长 / 分隔符）
- [ ] 长度域有自校验上限吗？（框架的 `MaxPackageLength` 比的是**已读字节数**，不是**你声明的长度**，帮不上忙）
- [ ] 脏数据是重同步还是断连？（**filter 抛异常 = 断连**）
- [ ] "数据不足"回退不消费，"脏数据"消费掉——分清了吗？
- [ ] 多段 `ReadOnlySequence` 处理对了吗？（`FirstSpan()` 只在单段成立）
- [ ] 编码器和解码器**长度语义一致**吗？（`LEN` 到底含不含帧尾）
- [ ] 编码器**大端还是小端**？（必须和客户端一致）
- [ ] **代码里的长度定义和文档一致吗？**（我在这上面栽过：文档写 n+6，代码是 n+7）

**命令层**
- [ ] 命令**显式注册**了吗？（`AddCommand<T>()`）
- [ ] `Key` **显式 `(byte)`** 了吗？（`Key = 0x01` 会启动即崩）
- [ ] 有 `RegisterUnknownPackageHandler` 吗？

**会话层**
- [ ] 自定义会话承载设备身份了吗？
- [ ] **单一写者**吗？（并发写 = 字节交错）—— 并且**所有出口都走同一个**发送方法
- [ ] 关闭事件里做的是**比较删除**吗？
- [ ] 重连时**踢掉旧连接**了吗？（且在**锁外**关闭）

**业务层**
- [ ] 在线状态是**业务心跳**判的，还是靠 `LastActiveTime`？（后者在高频数据下等于没有）
- [ ] 高频数据**不会刷新心跳时间**吧？
- [ ] 高频数据**不会把离线设备改回在线**吧？
- [ ] 设备身份**只绑定一次**吗？（每帧信任 = 可劫持）

**工程层**
- [ ] 客户端和服务端**共用同一个 filter / encoder** 吗？
- [ ] JSON 序列化配置**每层都统一**吗？（中文转义）
- [ ] 容器内监听地址是 `any` 吗？
- [ ] 配置项的**大小写**对吗？（`idleSessionTimeOut` 的 `Out` 是大写 O，写错静默用默认值）
- [ ] 多 Server 时 DI 是**共享实例**还是各一份？
- [ ] **文档和代码对得上吗？**（`Channel` vs `SemaphoreSlim`、`n+6` vs `n+7`）
- [ ] **测试是红的吗？** —— 这一条应该放在最前面

---

## 写在最后

回头看这些坑，你会看到一个规律：

> **SuperSocket 给你的全是"机制"，一个"策略"都不给。**
>
> 什么算在线、谁是设备、怎么防劫持、并发怎么写、脏数据怎么办 —— **全都要你自己定。**

框架选对了，能省掉大量机械劳动（会话管理、管道化读写、命令路由、宿主集成）。但剩下的部分一个都省不掉，**而这部分决定了你的系统是在 demo 里跑，还是能在机房里待一年。**

那些"起得起来"的坑（`Key` 的装箱、JSON 转义、文档 off-by-one）一测就发现；那些"出事了才暴露"的坑（并发写、重连误删、离线误判），**只能靠设计时想清楚 + 测试持续盯着**。

所以我想把这篇文章的重心放在两个地方：

1. **框架的边界在哪** —— 哪些是 SuperSocket 管的，哪些是你必须自己写的；
2. **代码的真实状态** —— 包括那 26 个失败的测试。

比起一篇看起来无懈可击的文章，我更希望这篇能让你在**照着做的时候顺手把自己的测试也写上** —— 因为上面那个"filter 其实没实现"的教训，只靠读代码是发现不了的。

---

*本文所有代码均来自仓库里的 `DeviceGateway` 项目（ASP.NET Core Web API + SuperSocket v2 命令模式设备接入网关），.NET 10 + SuperSocket 2.1.0。文中所有关于框架行为的判断（`FixedHeaderPipelineFilter` 的假设、`MaxPackageLength` 的比对对象、`LastActiveTime` 的刷新时机、`CommandAttribute.Key` 的类型）都通过反射读取 SuperSocket 2.1.0 程序集核对过，不是凭印象。*

*第十四节列出的 26 个失败测试是当前仓库的真实状态，不是笔误。*

```
dotnet build
dotnet test
dotnet run --project src/DeviceGateway.Server
dotnet run --project src/DeviceGateway.Client -- --count 2 --host 127.0.0.1 --port 5001
```

*欢迎交流指正。*
