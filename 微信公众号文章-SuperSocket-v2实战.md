# SuperSocket v2 实战：用它做一个能上生产的设备接入网关

> 这不是 SuperSocket 的官方文档翻译，而是我在 `DeviceGateway` 项目里踩过的坑。
> 所有 API 签名都对着 SuperSocket **2.1.0** 的程序集核对过（用反射 dump 出来的），所有代码都来自仓库里能跑起来的项目。

---

## 写在前面

做设备接入网关，SuperSocket v2 是个很好的起点 —— 它把 TCP 服务器的骨架给你了：会话管理、管道化读写、命令路由、还能和 ASP.NET Core 共用一个宿主。

但真正花时间的从来不是"连上"，而是**连上之后如何不出事**。

这篇文章我按"你从零开始用 SuperSocket 会遇到什么顺序"来组织：

| 你要写的 | 对应的 SuperSocket 机制 | 本文章节 |
|---|---|---|
| 服务端怎么起 | `AsSuperSocketHostBuilder` + `ConfigureHostBuilder` | 二 |
| 协议怎么拆包 | `IPipelineFilter` / `IPackageEncoder` | 三 |
| 命令怎么路由 | `[Command]` + `IKeyedPackageInfo` | 四 |
| 会话状态放哪 | `AppSession` 派生类 | 五 |
| 怎么往下发消息 | `IPackageEncoder` + `IAppSession.SendAsync` | 六 |
| 客户端怎么连 | `EasyClient` | 七 |
| 离线怎么判 | **框架帮不了你，得自己写** | 八 |

---

## 一、SuperSocket 给了什么，没给什么

先把边界划清楚，后面每一节都是在填这个边界。

**框架给你的（机制）**：

- TCP 监听、多监听器、backlog、Nagle 开关
- 每连接一个 `Session`，会话状态、连接生命周期事件
- **管道化读写** —— 字节流 → 消息包的拆包/粘包/半包处理
- 命令路由 —— 按包类型分发给不同的处理器
- 会话容器 —— 按 ID 或条件查找会话
- **和 ASP.NET Core 共用宿主**（同一个 DI、同一份配置、同一套日志）

**框架不给你（策略）**：

- 什么算"设备在线"？心跳多久算超时？
- 设备身份怎么认证？怎么防止被别人冒名？
- 脏字节是重同步还是断连？
- 并发写同一个 socket 怎么办？
- 长度域是外部输入，攻击面在哪？

> **一句话总结**：SuperSocket 全是"机制"，一个"策略"都不给。
> 框架选对了能省掉大量机械劳动，但剩下的部分一个都省不掉 —— **而这部分决定了你的系统是在 demo 里跑，还是能在机房里待一年。**

本文的项目是一个 .NET 10 + SuperSocket 2.1.0 的设备接入网关：

```
DeviceGateway/
├── src/
│   ├── DeviceGateway.Shared/     协议契约（服务端与客户端共用同一份实现）
│   │   ├── Protocol/             帧格式、拆包过滤器、编码器
│   │   └── Contracts/            JSON 配置、消息负载 DTO
│   ├── DeviceGateway.Server/     Web API + SuperSocket 服务端
│   │   ├── Commands/             心跳、位置数据命令
│   │   ├── Registry/             设备注册表
│   │   ├── Sessions/             设备会话
│   │   ├── Background/           离线看门狗
│   │   └── Controllers/          HTTP 接口
│   └── DeviceGateway.Client/     EasyClient 设备模拟器
└── tests/DeviceGateway.Tests/      协议拆包与设备注册表单元测试
```

**为什么单独抽一个 Shared**：帧格式只定义一次。客户端和服务端共用**同一个**过滤器与编码器，避免两边各写一套、逐渐漂移 —— 这是这类项目最容易出问题的地方。客户端按小端写、服务端按大端读，这种 bug 在联调前不会暴露。

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

**整帧长度 = `n + 7` 字节**（帧头 6 + 帧尾 1）。这个定义只有一处：

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

> ⚠️ **我自己栽过的 off-by-one**：文档里我最初写的是"整帧长度 = n + 6"，少算了帧尾那 1 个字节。代码里是 `n + 7`。这种错只写在文档里还不要紧 —— 一旦编码器和解码器各按各的理解写，就变成"设备发 24 字节、服务端等 25 字节"的死锁。**长度语义必须只有一个定义来源。**

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

### `AsSuperSocketHostBuilder<TPackage, TFilter>()`

我 dump 了 2.1.0 的 `HostBuilderExtensions`，它有 4 个重载：

```csharp
// SuperSocket.Server.Host.HostBuilderExtensions
ISuperSocketHostBuilder<TReceivePackage>              AsSuperSocketHostBuilder<TReceivePackage>(IHostBuilder)
ISuperSocketHostBuilder<TReceivePackage,TPipelineFilter> AsSuperSocketHostBuilder<TReceivePackage,TPipelineFilter>(IHostBuilder)
```

**两个泛型参数分别是消息包和拆包过滤器。这一步就把你的协议"钉"进了框架。**

只给包类型不给过滤器时，框架会用 `DefaultPipelineFilterFactory<TPackage, TFilter>` 按约定创建默认过滤器 —— 但默认那个是行分隔符/定长的，不是你要的二进制协议。

### 那句诡异的 `((IMinimalApiHostBuilder)...)` 是什么意思

这是**新手最容易卡住的地方之一**，我第一次写到这里也懵了。

`ISuperSocketHostBuilder` 继承自 `IMinimalApiHostBuilder`，而 `ConfigureHostBuilder()` 定义在父接口上：

```csharp
// SuperSocket.Server.Abstractions.Host
public interface IMinimalApiHostBuilder
{
    void ConfigureHostBuilder();
}

public interface ISuperSocketHostBuilder : IHostBuilder, IMinimalApiHostBuilder
{
    ISuperSocketHostBuilder ConfigureSupplementServices(Action<IServiceCollection, IServer> configureDelegate);
}
```

而 `AsMinimalApiHostBuilder()` 的返回值类型是 `IMinimalApiHostBuilder`：

```csharp
IMinimalApiHostBuilder AsMinimalApiHostBuilder(ISuperSocketHostBuilder hostBuilder);
```

**问题在于**：`ISuperSocketHostBuilder` 这个接口**本身没有暴露** `ConfigureHostBuilder()` —— 它在父接口 `IMinimalApiHostBuilder` 上。而 `IMinimalApiHostBuilder` 对 `ISuperSocketHostBuilder` 来说是个**显式接口实现**。

于是就有了这段看起来很怪、实际是**必需**的写法：

```csharp
// ❌ 编译不过：ISuperSocketHostBuilder 上没有 ConfigureHostBuilder
superSocketBuilder.ConfigureHostBuilder();

// ✅ 先转成接口类型，才能拿到显式实现的方法
((IMinimalApiHostBuilder)superSocketBuilder.AsMinimalApiHostBuilder()).ConfigureHostBuilder();
```

**这一行的作用是：把 SuperSocket 的配置"接回"ASP.NET Core 的宿主构建流程。** 没有它，`app.MapControllers()` 之类的 ASP.NET Core 配置就不会生效。

### 几个值得知道的扩展方法

这些方法分布在几个不同的类型上（都是 2.1.0 反射 dump 出来的）：

| 扩展方法 | 声明位置 | 作用 |
|---|---|---|
| `UseSession<TSession>()` | `ISuperSocketHostBuilder<T>` | 自定义会话类型（本项目用 `DeviceSession`） |
| `UsePipelineFilter<T>()` / `UsePipelineFilterFactory<T>()` | `ISuperSocketHostBuilder<T>` | 指定拆包过滤器 / 过滤器工厂 |
| `UsePackageDecoder<T>()` / `UsePackageEncoder<T>()` | `ISuperSocketHostBuilder<T>` | 指定包解码器 / 编码器 |
| `UseMiddleware<T>()` | `ISuperSocketHostBuilder<T>` | 插入中间件 |
| `UseCommand<T>(...)` | `CommandMiddlewareExtensions` | 启用命令模式 |
| `UseInProcSessionContainer()` | `InProcSessionContainerMiddlewareExtensions` | 启用进程内会话容器 |
| `UseClearIdleSession()` | `HostBuilderExtensions` | 启用空闲会话清理 |
| `UseSessionHandler(onConnected, onClosed)` | `HostBuilderExtensions` | 用委托代替派生会话类 |
| `UsePackageHandler(...)` | `HostBuilderExtensions` | 直接用委托接管收包，**不用命令模式** |
| `UseGZip()` | `HostBuilderExtensions` / `ISuperSocketHostBuilder<T>` | 启用压缩 |
| `ConfigureServerOptions(...)` | `ISuperSocketHostBuilder<T>` | 配置监听器等 |
| `ConfigureErrorHandler(...)` | `HostBuilderExtensions` | 配置错误处理 |

> **`UsePackageHandler` 值得单独提一句**：如果你不需要命令路由，用它挂一个委托就能收包，最轻量。本项目用命令模式是因为要按消息类型分发。
>
> **`UseSessionHandler` 也是个宝藏**：它让你不写会话子类也能拿到连接/断开事件 —— 对"先跑起来再说"的场景很友好。

### 监听配置

`ServerOptions` 和 `ListenOptions` 的属性名（我 dump 出来的）：

```csharp
// SuperSocket.Server.Abstractions.ServerOptions : ConnectionOptions
public string Name { get; set; }
public List<ListenOptions> Listeners { get; set; }
public Encoding DefaultTextEncoding { get; set; }
public int ClearIdleSessionInterval { get; set; }
public int IdleSessionTimeOut { get; set; }
public int PackageHandlingTimeOut { get; set; }
public bool EnableProxyProtocol { get; set; }

// SuperSocket.Server.Abstractions.ListenOptions
public string Ip { get; set; }
public int Port { get; set; }
public string Path { get; set; }
public int BackLog { get; set; }
public bool NoDelay { get; set; }
public ServerAuthenticationOptions AuthenticationOptions { get; set; }
```

对应到 `appsettings.json`：

```jsonc
"serverOptions": {
  "name": "DeviceGateway",
  "listeners": [ { "ip": "any", "port": 5001, "backLog": 100, "noDelay": true } ],
  "idleSessionTimeOut": 600,
  "clearIdleSessionInterval": 120
}
```

> ⚠️ **大小写陷阱**：`IdleSessionTimeOut` 的 `Out` 是**大写 O**。写成 `idleSessionTimeout`（小写 t）**不会报错**，只会静默使用默认值 —— 因为配置绑定是大小写不敏感的宽松匹配，但这个字段的宽松匹配恰好会把"看起来对"的写法接上，而真正写错的那个键则被忽略。
>
> 这类"静默失效"的配置错误最难查。建议：改完配置**一定用实际行为验证**（比如把超时设成 5 秒看会不会断），别只看日志。

### 共用宿主的价值

SuperSocket 的 TCP 监听和 ASP.NET Core 的 HTTP 监听跑在**同一个 `IHost`** 里：同一个 DI 容器、同一套配置、同一份日志、一个进程一个生命周期。

设备接入服务要同时提供 TCP 和 HTTP 接口时，这一点很值 —— 不用为了两个协议起两个进程、搞两套部署。

---

## 三、拆包：SuperSocket 留给你的最大一块

### 框架给了哪些内置过滤器

我 dump 了 `SuperSocket.ProtoBase` 的全部公开类型，可用的过滤器有：

| 过滤器 | 适用场景 |
|---|---|
| `LinePipelineFilter` | 按行分隔（`\r\n`），最简单 |
| `CommandLinePipelineFilter` | 命令行风格 |
| `FixedSizePipelineFilter<T>` | 定长包 |
| `FixedHeaderPipelineFilter<T>` | 定长帧头 + 长度域 |
| `BeginEndMarkPipelineFilter<T>` | 头尾标记分隔 |
| `PackagePartsPipelineFilter<T>` | 一个包由多个部分组成 |

**但自定义二进制协议，基本都得自己写 filter。** 下面讲为什么。

### `FixedHeaderPipelineFilter<T>` 为什么不够用

我用反射看了它的结构：

```
public virtual TPackageInfo Filter(SequenceReader<byte>& reader)
protected virtual int GetBodyLengthFromHeader(ref ReadOnlySequence<byte> buffer)
public virtual void Reset()
```

关键在基类 `Filter` 的实现：**它假定帧头就在序列的第 0 字节**（`reader.Sequence.Slice(0, headerSize)`），**没有"扫描下一个 `0x55`"的能力**。

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

第二段就是**流就此卡死**：基类把 `0x55` 前面那两个字节当成长度域，解析出一个 39124992 的荒谬长度，然后返回"消费 0 字节"。框架于是永远等更多数据。

表现就是某台设备莫名其妙不再上报了，服务端日志一片安静，连个异常都没有。

### `BeginEndMarkPipelineFilter<T>` 的两个问题

它按**第一个 `0xAA`** 截断：

1. **完全忽略长度字段**，只看头尾标记 —— 而 UTF-8 里的 `0xAA` 并不罕见，JSON 字符串里一个字符就可能踩中
2. 找不到帧头时它会抛 `ProtocolException`，而 SuperSocket 对 filter 抛异常的处理是**直接以 ProtocolError 断连** —— 在 filter 里抛异常 = 断连，不是容错

### 所以：自己写 filter

我 dump 出的接口是：

```csharp
// SuperSocket.ProtoBase
public interface IPipelineFilter<TPackageInfo> : IPipelineFilter
{
    IPackageDecoder<TPackageInfo> Decoder { get; set; }
    IPipelineFilter<TPackageInfo> NextFilter { get; }
    TPackageInfo Filter(ref SequenceReader<byte> reader);
}

public interface IPipelineFilter
{
    object Context { get; set; }
    void Reset();
}
```

**四条原则**：

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

这一条很多人会漏，而且漏得有道理 —— 因为**框架的 `MaxPackageLength` 检查根本帮不上忙**。我反射追了调用点，它在 `PipeConnectionBase.ReadBuffer` 里，比的是**从 socket 读进缓冲区的字节数**，不是**你在负载里声明的长度**。实测：声明一个 100MB 的长度，基类返回"消费 0 字节"，检查压根没触发。

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

### 你其实不用实现全部

`IPipelineFilter<T>` 要求你实现 `Filter` 和 `Reset`，但 `Decoder` 和 `NextFilter` 是属性。继承 `PipelineFilterBase<T>`（它把 `Filter` 声明为 `abstract`）能少写点样板代码。

> **一个小建议**：如果你要覆盖 `FixedHeaderPipelineFilter<T>.Filter`，反射显示它是 `public virtual` —— **技术上可以覆盖**。但覆盖之后基类只剩三个自动属性可用，继承的意义就没了，这时**不如直接实现 `IPipelineFilter<T>`**，语义更清楚。
>
> 我这个项目现在就是在重构的半路上：`DeviceFrameFilter` 一度改成了继承 `FixedHeaderPipelineFilter`（结果测试挂了 26 个），文件里那段完整实现被注释掉了，因为重命名时类名撞了。**这就是"重构改一半"最典型的样子。**

---

## 四、命令模式：`[Command]` + `IKeyedPackageInfo`

SuperSocket 的命令模式是它最好用的部分，概念也简单。

### 三步

**第一步：包类型实现 `IKeyedPackageInfo<byte>`**

```csharp
public sealed class DevicePackage : IKeyedPackageInfo<byte>
{
    public byte Key { get; set; }              // 消息类型字节
    public byte MessageType => Key;            // 业务语义别名
    public string Payload { get; set; } = "";  // JSON 负载（已是文本，不再二次序列化）
}
```

**第二步：命令类实现 `IAsyncCommand<TSession, TPackage>`**

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

**第三步：显式注册**

```csharp
.UseCommand(options =>
{
    options.AddCommand<HeartbeatCommand>();
    options.AddCommand<PositionCommand>();
})
```

### 这里有两个真实的坑

**坑 1：命令不会自动发现。**

你把命令类写好了、标好 `[Command]` 了，然后发现运行起来什么都没发生。原因：SuperSocket **不做命令的自动扫描发现**，`[Command]` 特性只是元数据，路由表是你手动往里塞的。

> 顺带说清楚：`CommandOptions` 有个 `Assemblies` 属性（`CommandAssemblyConfig[]`），**看起来**能自动扫描。**但别在多 Server 场景下用它** —— `CommandOptions.GetCommandTypes` 会**修改** `CommandSources` 列表，多 Server 时每个 Server 都绑同一个配置节 ⇒ 命令重复注册 ⇒ 启动抛 `Duplicated command with Key ...`。改用代码里 `AddCommandAssembly(...)`。

**坑 2：`Key` 写成裸的 `0x01` 会启动即崩。**

我反射 dump 了 `CommandAttribute`：

```csharp
public class CommandAttribute : Attribute
{
    public string Name { get; set; }
    public object Key { get; set; }   // ← 注意是 object，不是 byte
}
```

**`Key` 的类型是 `object`。** 我实测了装箱行为：

```
Key 类型 = System.Object
(byte)0x01 装箱后运行时类型 = System.Byte
裸 0x01 装箱后运行时类型   = System.Int32
裸 0x01 按 byte 拆箱 → InvalidCastException:
    Unable to cast object of type 'System.Int32' to type 'System.Byte'.
```

框架按 `byte` 拆箱 → `InvalidCastException` → **服务启动即崩**。正确写法必须显式转换：

```csharp
Key = (byte)DeviceMessageTypes.Heartbeat    // ✅
Key = 0x01                                  // ❌ 装箱成 int，启动崩
```

**这类"能编译、能启动前跑、启动就崩"的 bug 最耗时间。** 因为你会怀疑是不是配置写错了、包类型写错了 —— 实际上是 3 个字符的强制转换。

### `RegisterUnknownPackageHandler` —— 别忘了它

```csharp
options.RegisterUnknownPackageHandler<DevicePackage>((session, package, ct) =>
{
    session.GetDefaultLogger().LogWarning(
        "收到未支持的消息类型 0x{MsgType:X2}，负载：{Payload}，已忽略",
        package.MessageType, package.Payload);
    return ValueTask.CompletedTask;
});
```

设备端固件升级后发了个新类型，没有这个处理器的话行为不可控。**加上它 = 未支持的消息被记日志并忽略，而不是变成一个未处理异常。**

### 命令模式之外的两个选项

我 dump 了 `CommandOptionsExtensions`，除了 `AddCommand<T>()` 还有：

- **`AddCommandAssembly(Assembly)`** —— 按程序集批量注册命令（注意上面说的坑）
- **`AddGlobalCommandFilter<T>()`** —— 全局命令过滤器，做审计/限流/日志的统一入口

`CommandFilterAttribute` 有同步版（`OnCommandExecuting` 返回 `bool`，返回 false 可以**中断命令执行**）和异步版（`OnCommandExecutingAsync`）。**权限校验、限流这类横切逻辑放这里，比塞进每个命令里干净得多。**

---

## 五、会话：你的业务状态放哪

`AppSession` 是会话基类，我 dump 出的公开成员：

```csharp
public class AppSession : IAppSession, ILogger, ILoggerAccessor
{
    public string SessionID { get; }
    public DateTimeOffset StartTime { get; }
    public DateTimeOffset LastActiveTime { get; }
    public SessionState State { get; }
    public IServerInfo Server { get; }
    public object DataContext { get; set; }
    public EndPoint RemoteEndPoint { get; }
    public EndPoint LocalEndPoint { get; }
    public ILogger Logger { get; }

    public object this[object name] { get; set; }

    public virtual ValueTask CloseAsync();
    public virtual ValueTask CloseAsync(CloseReason reason);
}
```

**`DataContext` 和索引器 `this[name]` 都是放业务状态的现成位置** —— 不想派生类的话，直接往里塞。

本项目派生了一个 `DeviceSession`，承载设备身份绑定和发送锁：

```csharp
public sealed class DeviceSession : AppSession, ISessionHandle
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _bindLock = new();
    private int _isClosing;
    private string _deviceId = string.Empty;

    public string DeviceId => Volatile.Read(ref _deviceId);

    public bool TryBindDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        lock (_bindLock)
        {
            if (IsClosing)
                return false;

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

    protected override async ValueTask OnSessionClosedAsync(CloseEventArgs e)
    {
        if (Interlocked.Exchange(ref _isClosing, 1) == 1)
        {
            await base.OnSessionClosedAsync(e);
            return;
        }

        try { UnbindFromRegistry(); }
        finally { await base.OnSessionClosedAsync(e); }
    }
}
```

### 一个必须自己做的安全设计

**`deviceId` 在 JSON 负载里。如果每帧都信任 JSON 里的 `deviceId`，任何一个客户端只要写上 `DRONE-002`，就能劫持 DRONE-002 的会话绑定** —— 网关会把发给 DRONE-002 的消息送到攻击者手里。

所以规则是：

> **第一帧到达时绑定到会话上，之后每帧只做一致性校验，不一致就丢弃。**

一次性解决两个问题：**安全**（不能冒名）+ **性能**（绑定后不用每帧解析 JSON 取 deviceId）。

### `Logger` 的一个坑

`AppSession.Logger` 依赖挂在会话上的 `Server` —— **脱离宿主单独用（比如单元测试）时会抛 `NullReferenceException`**。

本项目的处理是保存一份可注入的日志器，没有注入时回退到框架的，并且**把日志失败兜住**：

```csharp
private void Log(LogLevel level, string message, params object?[] args)
{
    try
    {
        if (_logger != null) { _logger.Log(level, message, args); return; }
        Logger.Log(level, message, args);
    }
    catch
    {
        // 日志失败不能影响业务流程 —— 直接吞掉。
    }
}
```

> **顺带说个日志类别的问题**：`session.Logger` 的类别名是 **`SuperSocketService`**，**不是你的命令类名** —— 因为这个 logger 是框架内部创建的。所以要调级别得配：
>
> ```jsonc
> "Logging": { "LogLevel": { "SuperSocketService": "Debug" } }
> ```
>
> 我在这个项目里还犯过一个相关错误：把"设备上报"的日志写成 `Debug` 级别，结果**日志根本不打印** —— 因为默认配置下 `Debug` 是关闭的。**开发期要看的日志，就别写在默认不开的级别上。**

---

## 六、往下发消息：编码器 + 会话容器

### 编码器

`IPackageEncoder<T>` 只有一个方法：

```csharp
public interface IPackageEncoder<TPackage>
{
    int Encode(IBufferWriter<byte> writer, TPackage pack);
}
```

本项目的实现：

```csharp
public int Encode(IBufferWriter<byte> writer, DevicePackage package)
{
    var payloadLength = Encoding.UTF8.GetByteCount(package.Payload);

    // 预留整个帧的空间，一次性写入，避免多次 GetSpan/Advance 造成缓冲区碎片。
    var span = writer.GetSpan(DeviceFrame.TotalLength(payloadLength));

    var offset = 0;
    span[offset++] = DeviceFrame.BeginMark;

    BinaryPrimitives.WriteUInt32BigEndian(
        span.Slice(offset, DeviceFrame.LengthFieldSize), (uint)payloadLength);
    offset += DeviceFrame.LengthFieldSize;

    span[offset++] = package.MessageType;

    offset += Encoding.UTF8.GetBytes(package.Payload, span.Slice(offset, payloadLength));

    span[offset++] = DeviceFrame.EndMark;

    writer.Advance(offset);
    return offset;
}
```

**两个值得注意的点**：

1. **`GetSpan` 一次要够整个帧** —— 反复 `GetSpan`/`Advance` 会让缓冲区碎片化。
2. **负载不二次序列化** —— `package.Payload` 已经是 JSON 文本了，直接按 UTF-8 写字节。再 `JsonSerializer.Serialize` 一次会把它变成一个 JSON 字符串（带引号和转义）。

注册：

```csharp
builder.Services.AddSingleton<IPackageEncoder<DevicePackage>, DeviceFrameEncoder>();
```

### 发送：并发写会**字节交错**

这是**生产环境才偶发**的那种 bug。

场景：设备上报位置数据 → 服务端回复一条响应；同时 HTTP 接口触发跨设备消息 → 服务端往**同一个会话**写。两条路径同时往一个 socket 写，**TCP 的写如果并发进行，字节会交错**，发出去的是一帧拼接了另一帧半截的垃圾数据。

设备侧解析失败 → 断线 → 重连 → 又是垃圾数据。经典的"鬼故事"。

### 解法：单一写者（Single Writer）

每个会话一个发送锁 + **唯一一条写路径**：

```csharp
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

**双重检查（double check）不是多余的**：第一次在锁外快速失败；第二次在锁内，避免"等锁期间连接已经关了"。

> `((IAppSession)this)` 这个写法有点丑 —— `SendAsync(encoder, package)` 定义在 `IAppSession` 接口上，`AppSession` 没有把它暴露成公开方法，所以要显式转接口。

### 所有出口必须收敛到同一个方法

本项目 `DeviceSession` 有三个"看起来都能发送"的方法：

| 方法 | 作用 | 走发送锁 |
|---|---|---|
| `SendPackageAsync` | 唯一的真实发送出口 | ✅ |
| `SendSerializedAsync` | `SendPackageAsync` 的可测试形态 | ✅ |
| `ISessionHandle.SendAsync` | 注册表用的接口实现 | ✅ 转发 |

**第三个必须写成转发**：

```csharp
public async Task SendAsync(DevicePackage package, CancellationToken cancellationToken)
{
    await SendPackageAsync(package, cancellationToken);
}
```

如果有人图省事直接调 `((IAppSession)this).SendAsync(...)`，发送锁就被绕过去了 —— 并发写立刻回来。**"所有出口收敛到同一个方法"不是风格问题，是并发正确性的前提。**

### 怎么测"并发写不会交错"

直接测 socket 很难 —— 你没法稳定地制造竞态。做法是**把"持锁"和"怎么发"分开**：

```csharp
internal async Task SendSerializedAsync(
    DevicePackage package,
    Func<DevicePackage, CancellationToken, Task> transport,
    CancellationToken cancellationToken)
```

生产环境传真实 socket 发送；单元测试传一个记录用的假实现，然后**并发发起多次发送，检查假实现收到的包是否始终完整、顺序是否串行**。

**锁的逻辑因此可以被确定性地验证**，而不是靠压测碰运气。

### `Channel` 还是 `SemaphoreSlim`

我最初的设计是**有界 `Channel` + 唯一写循环**，队列满时丢弃最旧的，慢设备不会把 HTTP 请求拖住。最终落地的是 `SemaphoreSlim`：

- **`SemaphoreSlim`**：保证严格串行，语义更简单；代价是慢设备会阻塞发送方
- **`Channel`**：有背压，慢设备不阻塞发送方；代价是语义更复杂，且**一旦所有出口没收敛到同一个写循环，锁就白做了**

**如果你的场景里发送方不能被慢设备阻塞，就得用 `Channel`。** 这是明确的取舍。

### 会话容器：按设备找连接

跨设备消息需要知道"目标设备的 TCP 连接在哪"。`UseInProcSessionContainer()` 启用它（我反射确认它定义在 `SuperSocket.Server.Abstractions` 的 `InProcSessionContainerMiddlewareExtensions`）：

```csharp
public interface ISessionContainer
{
    IAppSession GetSessionByID(string sessionID);
    int GetSessionCount();
    IEnumerable<IAppSession> GetSessions(Predicate<IAppSession> criteria = null);
    IEnumerable<TAppSession> GetSessions<TAppSession>(Predicate<TAppSession> criteria = null);
}
```

> **注意 `ISessionContainer` 是同步的**，但框架还提供了异步版本 `IAsyncSessionContainer`（`GetSessionByIDAsync` 等），两者通过 `SessionContainerExtensions` 的 `ToSyncSessionContainer()` / `ToAsyncSessionContainer()` 互转。

本项目没用容器直接找会话 —— 而是自己维护 `DeviceRegistry`（`deviceId → session`）。**因为要的是"按设备标识"而不是"按会话 ID"查找**，容器帮不上这个忙。

### 一个跨设备消息的完整例子

```csharp
// 1. 找到目标设备的会话
var session = _registry.TryGetSession(request.To);
if (session is null)
{
    // 设备存在但离线 → 409；设备根本不存在 → 404
    return Conflict(new DeviceOfflineResponse { DeviceId = request.To });
}

// 2. 序列化负载（用统一的 JSON 配置，中文不转义）
var payloadJson = DeviceGatewayJson.Serialize(request.Payload ?? new { });

// 3. 打包成包，用 0x03（Report）类型下发
var package = new DevicePackage
{
    Key = DeviceMessageTypes.Report,
    Payload = payloadJson
};

// 4. 发送 —— 走会话的发送锁
await session.SendAsync(package, cancellationToken);
```

**注意这里的中文转义坑**（我踩过）：

```csharp
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder =
        System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    options.SerializerOptions.PropertyNamingPolicy =
        System.Text.Json.JsonNamingPolicy.CamelCase;
});
```

.NET 9+ 的默认是 `JavaScriptEncoder.Default`，会把所有非 ASCII 转义成 `\uXXXX`。而我们的流程是"HTTP 请求 → 序列化 → 放进 `Payload` → 原样转发给设备"，**中间没有任何一层做反转义** —— 设备收到的就是转义后的文本。

> **推论**：凡是把"用户输入的文本"原样透传到另一个系统的链路，**每一层的序列化配置都必须显式统一**。客户端和服务端要共用同一份 `JsonSerializerOptions`。这类 bug 在纯 ASCII 测试下永远测不出来。

---

## 七、客户端：`EasyClient`

客户端的 API 很简单，我 dump 出的 `IEasyClient`：

```csharp
public interface IEasyClient
{
    ValueTask<bool> ConnectAsync(EndPoint remoteEndpoint, CancellationToken cancellationToken = null);
    void StartReceive();
    ValueTask SendAsync(ReadOnlyMemory<byte> data);
    ValueTask SendAsync(ReadOnlySequence<byte> data);
    ValueTask SendAsync<TSendPackage>(IPackageEncoder<TSendPackage> packageEncoder, TSendPackage package);
    ValueTask CloseAsync();
    event EventHandler Closed;
}

public interface IEasyClient<TReceivePackage> : IEasyClient
{
    ValueTask ReceiveAsync();
    event PackageHandler<TReceivePackage> PackageHandler;
}

public interface IEasyClient<TSendPackage, TReceivePackage> : IEasyClient<TReceivePackage>
{
    ValueTask SendAsync(TSendPackage package);
}
```

本项目的用法：

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

### 三个必须记住的点

**1. `StartReceive()` 必须显式调用。**

`ConnectAsync` 只负责连接，接收循环要自己启动。忘了这行，客户端连上了但**永远收不到服务端的下行消息** —— 而且没有任何报错。

**2. `AsClient()` 不能省。**

`ConnectAsync` 在基类上是 `protected`，必须转成 `IEasyClient` 接口才能调用。

**3. 构造函数的泛型顺序是 `<TSendPackage, TReceivePackage>`。**

```csharp
public EasyClient<TReceivePackage>(IPipelineFilter<TReceivePackage> pipelineFilter)
public EasyClient<TSendPackage, TReceivePackage>(
    IPipelineFilter<TReceivePackage> pipelineFilter,
    IPackageEncoder<TSendPackage> packageEncoder,
    ILogger logger = null)
```

**注意第一个泛型是"发送包"，第二个是"接收包"。** 我们两边都是 `DevicePackage`，所以写成 `EasyClient<DevicePackage, DevicePackage>`。**如果写成反了，编译能过，但行为完全不对。**

### 模拟器的两个工程细节

- **重连要退避**：从 1 秒开始，每次翻倍，上限 30 秒。没有退避的无限重连，遇到服务端重启时会打成一片。
- **连上先发一次心跳**：让服务端尽快登记设备，不用干等第一个心跳周期。

```csharp
var backoff = TimeSpan.FromSeconds(1);
// ...
catch (Exception ex)
{
    Console.WriteLine($"[{_deviceId}] 连接异常: {ex.Message}，{backoff.TotalSeconds} 秒后重连");
    await Task.Delay(backoff, _cts.Token);
    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
}
```

---

## 八、离线判定：框架帮不了你

这个坑的隐蔽程度排第一。

SuperSocket 内置了 `UseClearIdleSession()`，看起来正好解决"设备长时间不活跃就清理"。**但它不能用来做业务离线判定。**

我反射追了 `LastActiveTime` 的更新点：

```
SuperSocket.Connection :: PipeConnection.<FillInputPipeAsync>d__13 calls UpdateLastActiveTime
SuperSocket.Connection :: PipeConnection.<ProcessOutputRead>d__14  calls UpdateLastActiveTime
```

**读和写都会刷新它。** 而 `LastActiveTime` 表达的是"**连接有 I/O**"，不是"**设备还活着**"。

我们的场景里：设备**每秒**发一包位置数据。于是：

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

**离线判定的实际延迟是 90～100 秒**（90 秒超时 + 最多 10 秒扫描间隔）。

### 配套的关键设计：位置数据**不应该**刷新心跳时间

这是整个项目里最反直觉的一个决定，但它决定了离线判定准不准。

如果位置数据也刷新心跳时间，那么：

```
设备心跳停了，但还在发位置数据
⇒ 心跳时间被位置数据不断刷新
⇒ 永远判不出离线
```

**高频数据会掩盖心跳缺失。** 直觉上"收到数据说明设备活着"，但恰恰相反：**要判活，就必须是那个"专用于判活"的数据。**

同理，位置数据**也不能把已判离线的设备改回在线**，否则看门狗每轮扫描把它标记离线、紧接着又被业务代码改回在线，**状态反复横跳**。

**这个设计最终落在代码里，是三个方法各管一件事**：

```csharp
// Bind —— 只回答"这条连接属于哪台设备"，明确不碰 Status，也不碰 LastHeartbeatAt
_devices[deviceId] = record with { ConnectedAt = now };

// TouchHeartbeat —— 只有心跳命令会调它，且只有当前绑定的 Session 才允许刷新
_devices[deviceId] = record with {
    LastHeartbeatAt = lastHeartbeatAt,
    Status = DeviceStatus.Online
};

// IsCurrentSession —— 位置命令用它识别"旧连接"，不刷新心跳、不改在线状态
```

于是 `PositionCommand` 里是这么写的：

```csharp
// 位置数据不刷新心跳时间、不改变在线状态 —— 离线判定只认心跳。
if (!_registry.IsCurrentSession(deviceId, session))
{
    session.LogWarning("设备位置数据来自旧连接，已忽略，DeviceId: {DeviceId}", deviceId);
    return;
}
```

> **换句话说：只有 `0x01` 心跳能让设备变成在线。**
> 只发位置数据、从不发心跳的设备在接口里一直显示离线 —— 这是刻意的。

---

## 九、多厂家 / 多协议：SuperSocket 的多 Server

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

我 dump 了 `MultipleServerHostBuilder` 的 API：

```csharp
public MultipleServerHostBuilder AddServer<TReceivePackage>(Action<ISuperSocketHostBuilder<TReceivePackage>> hostBuilderDelegate, string serverName = null)
public MultipleServerHostBuilder AddServer<TReceivePackage,TPipelineFilter>(Action<...> hostBuilderDelegate, string serverName = null)
public MultipleServerHostBuilder AddServer<TSuperSocketService,TReceivePackage,TPipelineFilter>(Action<...> hostBuilderDelegate, string serverName = null)
```

两个坑：

1. **配置节里不要写 `assemblies`**。多 Server 时每个 Server 都会绑定这个配置节，而 `CommandOptions.GetCommandTypes` 会**修改** `CommandSources` 列表 ⇒ 命令重复注册 ⇒ 启动抛 `Duplicated command with Key ...`。改用代码里 `AddCommandAssembly(...)`。
2. **多 Server 各有独立的 DI 容器**。普通 singleton 会变成**每个 Server 一个实例**，跨厂家互发消息会失败。要共享必须**注册同一个实例**，并用 `ReferenceEquals` 断言验证。

> `AdaptMultipleServerHost()` 来自 `SuperSocketExtensions`，是 `IHost` → 多 Server 宿主的适配器。**不调用它，多 Server 不会真正跑起来。**

---

## 十、跑起来看看

### 启动服务端

```bash
dotnet run --project src/DeviceGateway.Server
```

- 设备接入 TCP 端口：`5001`（配置在 `appsettings.json` 的 `serverOptions.listeners`）
- HTTP 接口端口：`launchSettings.json` 里是 **5223**（http）/ **7022**（https）

> TCP 端口和 HTTP 端口是**两套完全独立的配置**：TCP 在 `serverOptions.listeners`，HTTP 在 ASP.NET Core 自己的 `applicationUrl`。想固定用 8080 就显式指定：
>
> ```bash
> dotnet run --project src/DeviceGateway.Server --urls "http://localhost:8080"
> ```

### 启动模拟设备

```bash
dotnet run --project src/DeviceGateway.Client -- --count 2 --host 127.0.0.1 --port 5001
```

| 参数 | 默认值 | 说明 |
|---|---|---|
| `--count` | 1 | 模拟几台设备 |
| `--host` | 127.0.0.1 | 服务端地址 |
| `--prefix` | DRONE | 设备标识前缀（实际标识形如 `DRONE-001`） |
| `--heartbeat` | 10 | 心跳间隔（秒）。真实设备常用 30 秒，这里取折中值便于演示 |
| `--position` | 1 | 位置上报间隔（秒） |

### 调用接口

```bash
curl http://localhost:8080/api/Device/List
curl "http://localhost:8080/api/Device/Detail?deviceId=DRONE-001"

curl -X POST http://localhost:8080/api/Device/SendMessage -H "Content-Type: application/json" \
  -d '{"from":"DRONE-001","to":"DRONE-002","payload":{"msg":"hello"}}'

curl http://localhost:8080/api/Health/Check
```

| 场景 | 返回码 | 含义 |
|---|---|---|
| 发送成功 | 200 | 已发往目标设备（`status: "sent"`） |
| 目标设备不存在 | 404 | 设备从没上报过（`status: "unknown"`） |
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

## 十一、部署

### 容器内监听地址必须是 `any`

```jsonc
"listeners": [ { "ip": "any", "port": 5001, "backLog": 100, "noDelay": true } ]
```

**写 `127.0.0.1` 会导致容器外连不上** —— 容器里的 `127.0.0.1` 是容器自己，不是宿主机。

配置外置用 ASP.NET Core 的双下划线层级约定：

```bash
docker run -p 8080:8080 -p 5001:5001 \
  -e serverOptions__listeners__0__port=5001 \
  -e serverOptions__listeners__0__ip=any \
  -e deviceGateway__heartbeatTimeoutSeconds=90 \
  device-gateway:dev
```

Dockerfile 是多阶段构建、非 root 用户运行、带健康检查：

```dockerfile
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD ["/bin/sh", "-c", "curl -fsS http://127.0.0.1:8080/api/Health/Check || exit 1"]
```

### 分布式部署的边界

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

演进路径：**一致性哈希 + 转发**（前端按 `hash(deviceId)` 找归属副本）或 **共享注册表 + 发布订阅**（Redis 记 `deviceId → 副本`，HTTP 请求经 Pub/Sub 转发）。

> ⚠️ **不要用源 IP 做粘滞** —— NAT 后的设备共用 IP，重连还会换源端口。

---

## 十二、一份 SuperSocket 上手 checklist

如果你也要用 SuperSocket v2 做设备接入：

**接线**
- [ ] `AsSuperSocketHostBuilder<TPackage, TFilter>()` 两个泛型都给了吗？
- [ ] `((IMinimalApiHostBuilder)builder.AsMinimalApiHostBuilder()).ConfigureHostBuilder()` 调了吗？（漏了 ASP.NET Core 配置不生效）
- [ ] 监听配置项**大小写**对吗？（`idleSessionTimeOut` 的 `Out` 是大写 O，写错静默用默认值）

**命令层**
- [ ] 命令**显式注册**了吗？（`AddCommand<T>()`，`[Command]` 只是元数据）
- [ ] `Key` **显式 `(byte)`** 了吗？（`Key = 0x01` 会启动即崩）
- [ ] 有 `RegisterUnknownPackageHandler` 吗？
- [ ] 多 Server 时用 `AddCommandAssembly` 而不是配置节的 `assemblies` 了吗？

**协议层**
- [ ] 帧边界靠什么确定？（长度域 / 定长 / 分隔符）
- [ ] 长度域有自校验上限吗？（框架的 `MaxPackageLength` 比的是**已读字节数**，不是**你声明的长度**，帮不上忙）
- [ ] 脏数据是重同步还是断连？（**filter 抛异常 = 断连**）
- [ ] "数据不足"回退不消费，"脏数据"消费掉 —— 分清了吗？
- [ ] 多段 `ReadOnlySequence` 处理对了吗？（`FirstSpan()` 只在单段成立）
- [ ] 编码器和解码器**长度语义一致**吗？（`LEN` 到底含不含帧尾）
- [ ] 编码器**大端还是小端**？（必须和客户端一致）

**会话层**
- [ ] 自定义会话承载设备身份了吗？
- [ ] **单一写者**吗？（并发写 = 字节交错）—— 并且**所有出口都走同一个**发送方法
- [ ] 关闭事件里做的是**比较删除**吗？（`ReferenceEquals`）
- [ ] 重连时**踢掉旧连接**了吗？（且在**锁外**关闭）
- [ ] `AppSession.Logger` 在测试环境会 NPE —— 你处理了吗？

**业务层**
- [ ] 在线状态是**业务心跳**判的，还是靠 `LastActiveTime`？（后者在高频数据下等于没有）
- [ ] 高频数据**不会刷新心跳时间**吧？
- [ ] 高频数据**不会把离线设备改回在线**吧？
- [ ] 设备身份**只绑定一次**吗？（每帧信任 = 可劫持）

**客户端**
- [ ] `StartReceive()` 调了吗？（忘了 = 连上但收不到消息，且不报错）
- [ ] `EasyClient<TSend, TReceive>` 的泛型顺序对吗？
- [ ] 重连有**退避**吗？

**工程层**
- [ ] 客户端和服务端**共用同一个 filter / encoder** 吗？
- [ ] JSON 序列化配置**每层都统一**吗？（中文转义）
- [ ] 容器内监听地址是 `any` 吗？
- [ ] 多 Server 时 DI 是**共享实例**还是各一份？
- [ ] **文档和代码对得上吗？**（长度定义 n+6 还是 n+7？）
- [ ] **测试是红的吗？** —— 这一条应该放在最前面

---

## 写在最后

回头看这些坑，你会看到一个规律：

> **SuperSocket 给你的全是"机制"，一个"策略"都不给。**
>
> 什么算在线、谁是设备、怎么防劫持、并发怎么写、脏数据怎么办 —— **全都要你自己定。**

框架选对了，能省掉大量机械劳动（会话管理、管道化读写、命令路由、宿主集成、会话容器）。但剩下的部分一个都省不掉，**而这部分决定了你的系统是在 demo 里跑，还是能在机房里待一年。**

那些"起得起来"的坑（`Key` 的装箱、JSON 转义、`ConfigureHostBuilder` 那个怪写法）一测就发现；那些"出事了才暴露"的坑（并发写、重连误删、离线误判），**只能靠设计时想清楚 + 测试持续盯着**。

所以我想把这篇文章的重心放在两个地方：

1. **框架的边界在哪** —— 哪些是 SuperSocket 管的，哪些是你必须自己写的；
2. **API 的真实长相** —— 这篇文章里所有签名都是从 2.1.0 程序集反射 dump 出来的，不是凭印象。

比起一篇看起来无懈可击的文章，我更希望这篇能让你在**照着做的时候顺手把自己的测试也写上**。

---

*本文所有代码均来自仓库里的 `DeviceGateway` 项目（ASP.NET Core Web API + SuperSocket v2 命令模式设备接入网关），.NET 10 + SuperSocket 2.1.0。文中所有 API 签名（`HostBuilderExtensions`、`CommandOptionsExtensions`、`CommandAttribute`、`IPipelineFilter<T>`、`IPackageEncoder<T>`、`IAppSession`、`ISessionContainer`、`ServerOptions`、`ListenOptions`、`IEasyClient`）均通过反射读取 SuperSocket 2.1.0 程序集核对过；框架行为判断（`FixedHeaderPipelineFilter` 的假设、`MaxPackageLength` 的比对对象、`LastActiveTime` 的刷新时机、`CommandAttribute.Key` 的装箱行为）也都是实测的，不是凭印象。*

```
dotnet build
dotnet test
dotnet run --project src/DeviceGateway.Server
dotnet run --project src/DeviceGateway.Client -- --count 2 --host 127.0.0.1 --port 5001
```

*欢迎交流指正。*
