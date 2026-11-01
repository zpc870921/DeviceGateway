using DeviceGateway.Server.Background;
using DeviceGateway.Server.Commands;
using DeviceGateway.Server.Registry;
using DeviceGateway.Server.Sessions;
using DeviceGateway.Shared.Protocol;
using SuperSocket.Command;
using SuperSocket.ProtoBase;
using SuperSocket.Server;
using SuperSocket.Server.Abstractions.Host;
using SuperSocket.Server.Host;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// SuperSocket 服务端接线
//
// 这里的调用形态与仓库里已跑通的 supersocketdemo/Program.cs 保持一致：
//   AsSuperSocketHostBuilder<包类型, 过滤器类型>()
//     .UseSession<会话类型>()          —— 自定义会话，承载设备绑定等状态
//     .UseCommand(...)                  —— 命令模式，按包的 Key 路由
//     .UseInProcSessionContainer()      —— 会话容器（跨设备消息需要按会话查找）
//     .AsMinimalApiHostBuilder()        —— 与 ASP.NET Core 共用同一个宿主
// ---------------------------------------------------------------------------
var superSocketBuilder = builder.Host.AsSuperSocketHostBuilder<DevicePackage, DeviceFrameFilter>()
    .UseSession<DeviceSession>()
    //.UsePackageHandler<DevicePackage>((session, package) =>
    //{
    //    Console.WriteLine("收到数据包：");
    //    return ValueTask.CompletedTask;
    //})
    .UseCommand(options =>
    {
        // 命令不会被自动发现，必须显式注册。
        // Key 必须是 byte —— 写成裸的 0x01 会装箱成 int，拆箱时抛 InvalidCastException。
        options.AddCommand<HeartbeatCommand>();
        options.AddCommand<PositionCommand>();

        // 设备发来未支持的消息类型时走这里，而不是让服务端崩掉。
        options.RegisterUnknownPackageHandler<DevicePackage>((session, package, ct) =>
        {
            session.GetDefaultLogger().LogWarning(
                "收到未支持的消息类型 0x{MsgType:X2}，已忽略",
                package.MessageType);

            return ValueTask.CompletedTask;
        });
    })
    .UseInProcSessionContainer();

// ConfigureHostBuilder() 是 IMinimalApiHostBuilder 上的显式接口实现，
// 不能直接链式调用，要先把 builder 转成接口类型。
((IMinimalApiHostBuilder)superSocketBuilder.AsMinimalApiHostBuilder()).ConfigureHostBuilder();

// ---------------------------------------------------------------------------
// 应用服务注册
// ---------------------------------------------------------------------------

// 编码器：服务端下发消息时用。客户端用同一个 Shared 里的实现，保证两边格式一致。
builder.Services.AddSingleton<IPackageEncoder<DevicePackage>, DeviceFrameEncoder>();

// 让 ASP.NET Core 的 JSON 序列化与 Shared 里的配置保持一致。
//
// 这一步不是可有可无的：默认的 JSON 选项会把中文转义成 \uXXXX，
// 而请求体里的 payload 会被原样转发给设备，于是设备收到的是转义后的文本而不是原文。
// 统一用 UnsafeRelaxedJsonEscaping 后，整条链路（HTTP 请求 -> 设备负载）都是可读的原文。
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// 设备注册表：设备档案 + 在线会话。
builder.Services.AddSingleton<DeviceRegistry>();

// MVC 控制器（对外 HTTP 接口走 Controller/Action，不使用 Minimal API）。
builder.Services.AddControllers();

// 网关业务配置（心跳超时、扫描间隔）。
builder.Services.Configure<DeviceGatewayOptions>(
    builder.Configuration.GetSection("deviceGateway"));

// 离线看门狗。
builder.Services.AddHostedService<DeviceHeartbeatMonitor>();

var app = builder.Build();

// ---------------------------------------------------------------------------
// HTTP 接口
//
// 路由按仓库既有约定 api/[controller]/[action]：
//   GET  /api/Device/List            设备列表
//   GET  /api/Device/Detail?deviceId=xxx
//   POST /api/Device/SendMessage     下发跨设备消息
//   GET  /api/Health/Check            健康检查
// ---------------------------------------------------------------------------
app.MapControllers();

app.Run();
