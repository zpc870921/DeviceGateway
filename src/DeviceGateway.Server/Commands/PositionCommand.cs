using DeviceGateway.Server.Registry;
using DeviceGateway.Server.Sessions;
using DeviceGateway.Shared.Contracts;
using DeviceGateway.Shared.Protocol;
using SuperSocket.Command;
using SuperSocket.Server.Abstractions.Session;

namespace DeviceGateway.Server.Commands;

/// <summary>
/// 处理 0x02 无人机位置数据。设备每秒发一次。
/// <para>
/// 位置数据是"只看最新值"的语义 —— 补发一秒钟前的位置没有意义。
/// 所以这里写入"最新位置槽位"而不是排队，避免每秒每台设备都往队列里堆数据。
/// </para>
/// </summary>
[Command("Position", Key = (byte)DeviceMessageTypes.Position)]
public class PositionCommand : IAsyncCommand<DeviceSession, DevicePackage>
{
    /// <summary>
    /// 设备注册表。
    /// </summary>
    private readonly DeviceRegistry _registry;

    /// <summary>
    /// 构造位置命令。
    /// </summary>
    /// <param name="registry">设备注册表。</param>
    public PositionCommand(DeviceRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// 执行位置数据处理。
    /// </summary>
    /// <param name="session">设备会话。</param>
    /// <param name="package">消息包。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async ValueTask ExecuteAsync(DeviceSession session, DevicePackage package, CancellationToken cancellationToken)
    {
        if (!DevicePayloadReader.TryReadDeviceId(package, out var deviceId))
        {
            session.LogWarning("收到无法解析 deviceId 的位置消息，已忽略");
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // 校验设备标识、自动登记设备、把会话登记到注册表。
        if (! await _registry.TryBind(session, deviceId, now))
        {
            return;
        }

        // 位置数据<b>不</b>刷新心跳时间、<b>不</b>改变在线状态 —— 离线判定只认心跳。
        //
        // 这里只做"是不是当前绑定"的校验：设备重连后，旧连接上还在跑的数据处理任务
        // 不应该继续产生业务效果。用 IsCurrentSession 而不是 TouchHeartbeat，
        // 正是因为后者会顺带刷新心跳时间 —— 那会让"心跳停了但还在发位置数据"的设备
        // 永远判不出离线。
        if (!_registry.IsCurrentSession(deviceId, session))
        {
            session.LogWarning(
                "设备位置数据来自旧连接，已忽略，DeviceId: {DeviceId}",
                deviceId);

            return;
        }

        // 设备是否在线由两件事决定：心跳是否超时（看门狗负责）、连接是否还在（会话负责）。
        // 位置数据两者都不改变。
        //
        // 把收到的数据打印出来。
        //
        // 用 Information 级别而不是 Debug：默认配置下 Debug 是关闭的，
        // 那样这条日志根本不会出现在控制台里，等于没打印。
        session.LogInformation(
            "收到设备 {DeviceId} 的位置数据，负载：{Payload}",
            deviceId, package.Payload);
    }
}
