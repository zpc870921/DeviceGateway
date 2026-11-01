# DeviceGateway — .NET 10 + SuperSocket v2 多厂家设备接入网关

用 ASP.NET Core Web API + SuperSocket v2 命令模式实现设备接入网关。
当前实现了一个厂家的自定义二进制协议，架构上支持接入多个厂家、多种协议。

---

## 解决方案结构

```
multihostdemo/
├── DeviceGateway.slnx
├── src/
│   ├── DeviceGateway.Shared/     协议契约（服务端与客户端共用同一份实现）
│   │   ├── Protocol/             帧格式、拆包过滤器、编码器
│   │   └── Contracts/            消息负载 DTO、JSON 配置
│   ├── DeviceGateway.Server/     Web API + SuperSocket 服务端
│   │   ├── Commands/             心跳、位置数据命令
│   │   ├── Registry/             设备注册表
│   │   ├── Sessions/             设备会话
│   │   ├── Background/           离线看门狗
│   │   └── Controllers/          HTTP 接口（Controller/Action）
│   └── DeviceGateway.Client/     EasyClient 设备模拟器
└── tests/
    └── DeviceGateway.Tests/      协议拆包与设备注册表单元测试
```

**为什么单独抽 Shared**：帧格式只定义一次。客户端和服务端共用**同一个**过滤器与编码器，
避免两边各写一套、逐渐漂移 —— 这是这类项目最容易出问题的地方。

---

## 协议格式

```
0x55 | LEN(4字节大端) | TYPE(1字节) | JSON(n字节) | 0xAA
```

| 字段 | 说明 |
|---|---|
| `0x55` | 帧起始标记 |
| `LEN` | **JSON 负载的字节数 n**（不含类型字节、不含长度域本身）。4 字节**大端序** |
| `TYPE` | 消息类型：`0x01` 心跳、`0x02` 位置数据、`0x03` 通用上报 |
| `JSON` | UTF-8 编码的 JSON 负载 |
| `0xAA` | 帧结束标记 |

整帧长度 = `n + 6` 字节。

### 拆包为什么自己实现

没有用 SuperSocket 自带的 filter，原因：

- `FixedHeaderPipelineFilter<T>` 把帧头当作**当前位置的固定长度前缀**，没有扫描下一个
  `0x55` 的能力，遇到脏字节无法重同步。
- 它的长度钩子以**负返回值**表示"致命错误"，框架收到就抛 `ProtocolException` ——
  而 SuperSocket 对 filter 抛异常的处理是**直接以 ProtocolError 断连**。
  本协议里"长度域非法"只是脏数据，必须重同步而不是断连。
- `BeginEndMarkPipelineFilter<T>`（仓库里 `supersocketdemo` 用的那个）按**第一个 `0xAA`** 截断，
  **忽略长度字段** —— JSON 里出现 `0xAA` 字节就会错位；而且找不到帧头时它会抛
  `ProtocolException`，同样会断连。

`DeviceFrameFilter` 直接实现 `IPipelineFilter<DevicePackage>`，做法：

- **帧边界由长度域确定**，帧尾字节只做校验、不参与查找 ⇒ JSON 里含 `0xAA` 也不会截断。
- **脏数据只重同步、绝不抛异常** ⇒ 抛异常 = 断连，不是容错。
- **无状态**：每次调用都从 reader 当前位置重新解析，天然兼容框架
  "解出整帧后调 `Reset()`、半帧时不调" 的行为差异。
- **数据不足时消费 0 字节** —— 框架会把过滤器消费掉的字节从累积缓冲里丢弃，
  半消费会让已收到的部分帧永远拼不回来。
- **脏字节要及时吃掉** —— 否则累积缓冲会一直涨到超过框架的 `MaxPackageLength`（1 MB），
  然后被框架以协议错误断开。
- **长度域自校验上限**（64 KB）：框架自带的 `MaxPackageLength` 检查用的是已消费字节数，
  不是你声明的长度 ⇒ 伪造的巨大长度必须自己拦。

> 曾经把 `DeviceFrameFilter` 改成继承 `FixedHeaderPipelineFilter<T>`，但基类的三条前提
> （帧头固定取第 0 字节 / 解码失败也前进 / 状态存在实例字段）与"脏数据必须重同步且不断连"
> 直接冲突。继承它就得把 `Filter` 整个覆盖掉，那时基类只剩三个自动属性可用 ——
> 所以改回直接实现接口。

---

## 关键设计决策

### 设备身份：只绑定一次，之后只校验

`deviceId` 在 JSON 里。第一帧到达时**绑定**到会话上，之后每帧的 `deviceId` 只做**一致性校验**，
不一致就丢弃。

- 安全：任何客户端都不能在 JSON 里写别人的 `deviceId` 来劫持会话绑定。
- 性能：绑定之后不用每帧都解析 JSON 取 `deviceId`（设备每秒一包）。

### 离线判定：心跳超时，不靠连接空闲

**没有用 `UseClearIdleSession()` 做业务离线判定。** 它的依据是 `LastActiveTime`，
而这个值在**每次 socket 读写成功时都会刷新** —— 它表示"连接有 I/O"，不是"设备还活着"。
本项目设备每秒发一包位置数据 ⇒ `LastActiveTime` 永远是 1 秒前 ⇒ **那个超时永远不会触发**，
心跳已停但连接还挂着的设备会永远不被清理。

所以：

- **业务在线状态**由 `DeviceHeartbeatMonitor` 按心跳超时判定（默认 90 秒 = 3 个心跳周期）。
- **位置数据不刷新心跳时间** —— 否则高频数据会掩盖心跳缺失，判不出真离线。
  同理，位置数据也不会把已判离线的设备改回在线。
- **绑定会话也不改在线状态** —— `Bind` 只回答"这条连接属于哪台设备"，
  在线与否只看心跳。这样看门狗判离线之后，设备只要还在发位置数据就不会被改回在线。
- **连接断开立即置离线** —— 比等心跳超时更快更准。
- `UseClearIdleSession` 只作为回收僵死 socket 的**另一层兜底**。

> 换句话说：**只有 `0x01` 心跳能让设备变成在线**。
> 只发位置数据、从不发心跳的设备在接口里一直显示离线 —— 这是刻意的，
> 心跳才是"设备还活着"的唯一信号。

### 会话出口：单一写者

跨设备消息（HTTP 触发）和设备上报响应可能同时写同一个会话，并发写会导致**字节交错、帧损坏**。
所以每个会话有一个有界 `Channel` + **唯一一个写循环**，所有发送都排队走它。

副作用是好的：慢设备不会把 HTTP 请求拖住（队列满时丢弃最旧的）。

### 重连：踢掉旧连接，比较删除

- 同一设备重连时，主动关闭旧连接 —— 否则一个设备占着两条连接，推消息给哪条不确定。
- 注销用**比较删除**（`ConcurrentDictionary` 的 `ICollection<KeyValuePair<>>.Remove`）：
  只有当前绑定的确实是自己时才删。
  否则设备重连后，旧会话的关闭事件会把新会话的绑定误删，导致"设备在线但找不到"。

---

## 消息类型路由

`DevicePackage` 实现 `IKeyedPackageInfo<byte>`，`Key` 就是帧里的类型字节。
SuperSocket 的命令模式按 `Key` 路由到对应命令：

```csharp
[Command("Heartbeat", Key = (byte)DeviceMessageTypes.Heartbeat)]
public class HeartbeatCommand : IAsyncCommand<DeviceSession, DevicePackage>
```

> **`Key` 必须显式写 `(byte)`**：属性类型是 `object`，写成裸的 `0x01` 会装箱成 `int`，
> 拆箱时抛 `InvalidCastException` ⇒ **启动即崩**。

> **命令不会自动发现**，必须 `options.AddCommand<T>()` 显式注册。

---

## 运行

### 启动服务端

```bash
dotnet run --project src/DeviceGateway.Server
```

- 设备接入 TCP 端口：`5001`（配置在 `appsettings.json` 的 `serverOptions.listeners`）
- HTTP 接口端口：`8080`（`--urls` 指定）

### 启动模拟设备

```bash
# 模拟 2 台设备
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

### 观察心跳与位置数据

设备上报的数据在服务端会写一条 **Debug** 日志。
`appsettings.Development.json` 里已经把 `SuperSocketService` 类别开到了 Debug
（`session.Logger` 的类别就是它 —— 框架内部创建的，不是命令类的名字）：

```
Session[5e7b5c48-...]: 收到设备 DRONE-001 的心跳
Session[5e7b5c48-...]: 收到设备 DRONE-001 的位置数据: {"deviceId":"DRONE-001","latitude":30.118...,"longitude":120.657...}
```

> 设备每秒上报一次位置，所以用 `Information` 级别会刷屏 —— 生产环境保持默认的
> `Information` 即可，Debug 只在开发时开启。

### 调用接口

对外接口走 Controller/Action，路由按仓库既有约定 `api/[controller]/[action]`：

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/Device/List` | 设备列表（含在线状态、最后心跳时间） |
| GET | `/api/Device/Detail?deviceId=xxx` | 单个设备详情 |
| POST | `/api/Device/SendMessage` | 下发跨设备消息 |
| GET | `/api/Health/Check` | 健康检查 |

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
| 发送成功 | 200 | 已入队发往目标设备 |
| 目标设备不存在 | 404 | 设备从没上报过，网关不认识它 |
| 目标设备离线 | 409 | 设备存在但当前不在线 |
| 参数非法（空、给自己发） | 400 | — |

---

## 测试

```bash
dotnet test
```

130 个用例，全部不依赖 socket（跑完约 0.5 秒）。分五个方向：

| 文件 | 覆盖 |
|---|---|
| `DeviceFrameFilterTests` | 完整帧、逐字节分片、粘包、脏字节重同步、假标记带合法长度、帧尾校验、长度越界、非连续（多段）缓冲区、无状态性、**任何畸形输入都不抛异常** |
| `DeviceFrameValidationTests` | 帧头 / 长度 / 帧尾三项验证，以及三项全坏时的行为 |
| `DeviceFrameFragmentationTests` | 分片与脏数据混合场景（半帧 + 垃圾、垃圾前后、连续垃圾洪水） |
| `DeviceFrameFrameworkContractTests` | **过滤器是否满足 SuperSocket 的调用契约**：返回 null 必须消费 0 字节、绝不抛异常（抛异常 = `CloseReason.ProtocolError` 断连）、脏字节及时吃掉、累积缓冲不无限增长 |
| `DeviceRegistryTests` / `DeviceRegistryConcurrencyTests` | 自动添加、重连踢旧连接、比较删除、心跳刷新与超时判定、并发绑定/注销/心跳的竞态 |
| `DeviceSessionConcurrencyTests` / `DeviceSessionLifecycleTests` | 设备身份绑定一致性、**并发发送必须串行**、关闭语义、断开时的注销与重连竞态 |
| `PositionCommandTests` | **位置数据不刷新心跳、不把离线设备改回在线** —— 这条锁的是「离线只看心跳」这条需求 |

> `tests/DeviceGateway.Tests/Support/FrameDecoder.cs` 是所有协议测试共用的解码辅助方法，
> 它**逐行对照** `SuperSocket.Connection.PipeConnectionBase.ReadBuffer` 的语义实现。
> 协议测试通过它驱动，等于同时在验证过滤器满足框架契约。

### 手工验证分片与脏数据

```bash
dotnet run --project src/DeviceGateway.Server --urls "http://127.0.0.1:8080"
```

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

然后 `curl http://127.0.0.1:8080/api/Device/List` 确认 `SPLIT-001` 已登记。
在前面加几个垃圾字节（如 `b"\xde\xad\xbe\xef"`）再试一次 —— 设备仍应被正确登记，
服务端日志里也不应出现 `Protocol error`。

---

## Docker 部署

```bash
docker build -f src/DeviceGateway.Server/Dockerfile -t device-gateway:dev .
docker run -p 8080:8080 -p 5001:5001 device-gateway:dev
```

配置通过环境变量覆盖（ASP.NET Core 的双下划线层级约定）：

```bash
docker run -p 8080:8080 -p 5001:5001 \
  -e serverOptions__listeners__0__port=5001 \
  -e serverOptions__listeners__0__ip=any \
  -e deviceGateway__heartbeatTimeoutSeconds=90 \
  device-gateway:dev
```

> **容器内监听地址必须是 `any`（0.0.0.0）**。写 `127.0.0.1` 会导致容器外连不上 ——
> 这是容器化最常见的坑。

---

## 分布式部署的边界（请先读这一节）

**"Docker 部署" 和 "多副本横向扩展" 是两件事。** 本项目目前完成的是前者：

| 项目 | 状态 |
|---|---|
| 容器化（Dockerfile、健康检查、配置外置） | ✅ 已完成 |
| 单副本运行 | ✅ 可用 |
| 多副本横向扩展 | ⚠️ **当前不支持**，原因见下 |

### 为什么多副本还不行

设备注册表是**进程内内存**状态。多副本部署时：

- 设备连到副本 A → 副本 A 的注册表里有这个会话
- HTTP 请求打到副本 B → 副本 B 查不到该设备 → 跨设备消息发不出去

### 后续演进路径（本期未实现）

1. **一致性哈希 + 转发**（推荐）：前端读第一帧拿到 `deviceId`，按 `hash(deviceId)` 找到归属副本，
   把 TCP 流转发过去。这样"一个设备的会话永远在同一个副本"，HTTP 请求也按同样的哈希路由。
2. **共享注册表 + 发布订阅**：各副本把 `deviceId → 副本` 记在 Redis 里，
   HTTP 请求经 Redis Pub/Sub 转发到持有该会话的副本。

> **不要用源 IP 做粘滞** —— NAT 后的设备共用 IP，重连还会换源端口。

---

## 接入新厂家

每个厂家协议 = 一个独立的包类型 + 一个独立的过滤器 + 一个独立的监听端口。
参照仓库里已跑通的 `ssdemo01` 多服务模板：

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

**接新厂家时不要改现有厂家的任何代码。**

两个坑：

1. **`serverOptions:commands` 配置节不要写 `assemblies`**：多 Server 时每个 Server 都会绑定
   这个节，而 `CommandOptions.GetCommandTypes` 会**修改** `CommandSources` 列表 ⇒ 命令重复注册 ⇒
   **启动抛 "Duplicated command with Key ..." 异常**。改用代码里 `AddCommandAssembly(...)`。
2. **多 Server 各有独立的 DI 容器**：普通 singleton 会变成**每个 Server 一个实例**，
   跨厂家互发消息会失败。要共享必须**注册同一个实例**，并用 `ReferenceEquals` 断言验证。

---

## 与仓库里已有项目的关系

父目录 `D:\repos\supersocket_demo\` 下已有几个相关项目，本项目是它们的整理与演进：

| 已有项目 | 借鉴了什么 |
|---|---|
| `supersocketdemo/` | 同一个 0x55/0xAA 协议的原始实现（其 `BeginEndMarkPipelineFilter` 的两个坑，本项目已修正） |
| `ssdemo01/` | 多厂家多协议的服务编排模板 |
| `ConsoleApp3/` | 设备注册表与重连处理的写法 |
| `tcpclient/` | 帧格式的裸 TCP 实现（本项目改用 EasyClient） |

## client使用方法
dotnet run --project src/DeviceGateway.Client -- --count 2 --host 127.0.0.1 --port 5001