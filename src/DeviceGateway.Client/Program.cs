using DeviceGateway.Client;
using DeviceGateway.Shared.Contracts;
using DeviceGateway.Shared.Protocol;
using SuperSocket.Client;
using System.Buffers.Binary;
using System.Net;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

// 控制台默认用系统 OEM 代码页，中文会显示成乱码。这里显式切到 UTF-8。
// （这只是显示问题 —— 协议本身的 UTF-8 字节是正确的。）
Console.OutputEncoding = Encoding.UTF8;

// ---------------------------------------------------------------------------
// 设备模拟器
//
// 用法：
//   dotnet run --project src/DeviceGateway.Client -- --count 2 --host 127.0.0.1 --port 5001
//
// 参数：
//   --count N          模拟几台设备（默认 1）
//   --host <ip>        服务端地址（默认 127.0.0.1）
//   --port <port>      服务端端口（默认 5001）
//   --prefix <前缀>    设备标识前缀（默认 DRONE）
//   --heartbeat <秒>   心跳间隔秒数（默认 10）
//   --position <秒>    位置上报间隔秒数（默认 1）
// ---------------------------------------------------------------------------

var options = CommandLineOptions.Parse(args);

Console.WriteLine($"设备模拟器启动：{options.Count} 台设备 -> {options.Host}:{options.Port}");
Console.WriteLine($"心跳间隔 {options.HeartbeatSeconds} 秒，位置上报间隔 {options.PositionSeconds} 秒");

var endpoint = new IPEndPoint(IPAddress.Parse(options.Host), options.Port);


var simulators = new List<DeviceSimulator>();

for (var i = 0; i < options.Count; i++)
{
    // 设备标识：前缀 + 序号，例如 DRONE-001
    var deviceId = $"{options.DevicePrefix}-{i + 1:D3}";

    simulators.Add(new DeviceSimulator(deviceId, endpoint));
}

// 所有设备并行运行，各自独立重连。
var tasks = simulators.Select(s => s.RunAsync(
    TimeSpan.FromSeconds(options.HeartbeatSeconds),
    TimeSpan.FromSeconds(options.PositionSeconds)));

try
{
    await Task.WhenAll(tasks);
}
catch (OperationCanceledException)
{
    // Ctrl+C 取消，属正常退出。
}
finally
{
    foreach (var simulator in simulators)
    {
        await simulator.DisposeAsync();
    }
}

/// <summary>
/// 命令行参数。
/// </summary>
internal class CommandLineOptions
{
    /// <summary>模拟设备数量。</summary>
    public int Count { get; set; } = 1;

    /// <summary>服务端地址。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>服务端端口。</summary>
    public int Port { get; set; } = 5001;

    /// <summary>设备标识前缀。</summary>
    public string DevicePrefix { get; set; } = "DRONE";

    /// <summary>
    /// 心跳间隔（秒）。默认 10 秒 —— 真实设备常用 30 秒，
    /// 但演示时 30 秒要等太久才能观察到心跳更新，这里取折中值。
    /// </summary>
    public int HeartbeatSeconds { get; set; } = 10;

    /// <summary>位置上报间隔（秒）。</summary>
    public int PositionSeconds { get; set; } = 1;

    /// <summary>
    /// 解析命令行参数。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>解析结果。</returns>
    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();

        for (var i = 0; i < args.Length - 1; i++)
        {
            var key = args[i];
            var value = args[i + 1];

            switch (key)
            {
                case "--count":
                    options.Count = int.Parse(value);
                    break;
                case "--host":
                    options.Host = value;
                    break;
                case "--port":
                    options.Port = int.Parse(value);
                    break;
                case "--prefix":
                    options.DevicePrefix = value;
                    break;
                case "--heartbeat":
                    options.HeartbeatSeconds = int.Parse(value);
                    break;
                case "--position":
                    options.PositionSeconds = int.Parse(value);
                    break;
            }
        }

        return options;
    }
}
