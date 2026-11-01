using DeviceGateway.Shared.Contracts;
using DeviceGateway.Shared.Protocol;
using SuperSocket.Client;
using SuperSocket.ProtoBase;
using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace DeviceGateway.Client;

/// <summary>
/// 一台模拟设备。
/// <para>
/// 行为：每 30 秒发一次心跳（0x01），每秒发一次位置数据（0x02）。
/// 连接断开后自动重连，重连后重新发身份帧让服务端重新绑定本设备。
/// </para>
/// </summary>
public class DeviceSimulator : IAsyncDisposable
{
    /// <summary>
    /// 本设备的标识。
    /// </summary>
    private readonly string _deviceId;

    /// <summary>
    /// 服务端地址。
    /// </summary>
    private readonly IPEndPoint _endpoint;

    /// <summary>
    /// 出站编码器。
    /// </summary>
    private readonly IPackageEncoder<DevicePackage> _encoder = new DeviceFrameEncoder();

    /// <summary>
    /// 随机数发生器，用来让位置数据看起来在动。
    /// </summary>
    private readonly Random _random = new();

    /// <summary>
    /// 取消令牌源，Dispose 时取消。
    /// </summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// 模拟设备的构造函数。
    /// </summary>
    /// <param name="deviceId">设备标识。</param>
    /// <param name="endpoint">服务端地址。</param>
    public DeviceSimulator(string deviceId, IPEndPoint endpoint)
    {
        _deviceId = deviceId;
        _endpoint = endpoint;
    }

    /// <summary>
    /// 运行设备模拟：连接、发心跳、发位置数据、断线重连。
    /// </summary>
    /// <param name="heartbeatInterval">心跳间隔。</param>
    /// <param name="positionInterval">位置上报间隔。</param>
    public async Task RunAsync(TimeSpan heartbeatInterval, TimeSpan positionInterval)
    {
        var backoff = TimeSpan.FromSeconds(1);

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(heartbeatInterval, positionInterval);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_deviceId}] 连接异常: {ex.Message}，{backoff.TotalSeconds} 秒后重连");

                try
                {
                    await Task.Delay(backoff, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // 退避重连，最长 30 秒。
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    /// <summary>
    /// 建立一次连接并持续运行，直到连接断开。
    /// </summary>
    /// <param name="heartbeatInterval">心跳间隔。</param>
    /// <param name="positionInterval">位置上报间隔。</param>
    private async Task RunSessionAsync(TimeSpan heartbeatInterval, TimeSpan positionInterval)
    {
        // ConnectAsync 在底层是 protected，必须通过 AsClient() 拿到接口来调用。
        var client = new EasyClient<DevicePackage, DevicePackage>(
            new DeviceFrameFilter(),
            _encoder).AsClient();

        client.PackageHandler += (_, package) =>
        {
            // 服务端下发的消息（比如别的设备发过来的）。
            Console.WriteLine($"[{_deviceId}] 收到服务端消息 类型=0x{package.MessageType:X2} 内容={package.Payload}");
            return ValueTask.CompletedTask;
        };

        client.Closed += (_, _) =>
        {
            Console.WriteLine($"[{_deviceId}] 连接已断开");
        };

        var connected = await client.ConnectAsync(_endpoint, _cts.Token);
        if (!connected)
        {
            throw new InvalidOperationException($"连接服务端 {_endpoint} 失败");
        }

        client.StartReceive();

        Console.WriteLine($"[{_deviceId}] 已连接到 {_endpoint}");

        // 先立刻发一次心跳，让服务端尽早登记本设备。
        await SendHeartbeatAsync(client);

        using var heartbeatTimer = new PeriodicTimer(heartbeatInterval);
        using var positionTimer = new PeriodicTimer(positionInterval);

        // 心跳循环
        var heartbeatTask = Task.Run(async () =>
        {
            try
            {
                while (await heartbeatTimer.WaitForNextTickAsync(_cts.Token))
                {
                    await SendHeartbeatAsync(client);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出。
            }
        });

        // 位置数据循环
        var positionTask = Task.Run(async () =>
        {
            try
            {
                while (await positionTimer.WaitForNextTickAsync(_cts.Token))
                {
                    await SendPositionAsync(client);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出。
            }
        });

        // 等到连接断开或被取消。
        await Task.WhenAny(
            Task.Delay(Timeout.Infinite, _cts.Token),
            heartbeatTask,
            positionTask);

        _cts.Cancel();

        await client.CloseAsync();
    }



    /// <summary>
    /// 发送心跳（0x01）。
    /// </summary>
    /// <param name="client">客户端。</param>
    private async Task SendHeartbeatAsync(IEasyClient<DevicePackage, DevicePackage> client)
    {
        var payload = DeviceGatewayJson.Serialize(new HeartbeatPayload
        {
            DeviceId = _deviceId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        await client.SendAsync(_encoder, new DevicePackage
        {
            Key = DeviceMessageTypes.Heartbeat,
            Payload = payload
        });

    }

    /// <summary>
    /// 发送位置数据（0x02）。
    /// </summary>
    /// <param name="client">客户端。</param>
    private async Task SendPositionAsync(IEasyClient<DevicePackage, DevicePackage> client)
    {
        // 在基准点附近随机漂移，模拟设备在移动。
        var payload = DeviceGatewayJson.Serialize(new PositionPayload
        {
            DeviceId = _deviceId,
            Latitude = 30.123456 + (_random.NextDouble() - 0.5) * 0.01,
            Longitude = 120.654321 + (_random.NextDouble() - 0.5) * 0.01,
            Altitude = 100 + _random.NextDouble() * 50,
            Speed = _random.NextDouble() * 20,
            Heading = _random.NextDouble() * 360,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        await client.SendAsync(_encoder, new DevicePackage
        {
            Key = DeviceMessageTypes.Position,
            Payload = payload
        });
    }

    /// <summary>
    /// 释放资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        // 给运行中的循环一点时间收尾。
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        catch
        {
            // 忽略等待中的异常。
        }

        _cts.Dispose();
    }
}
