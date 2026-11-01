using DeviceGateway.Server.Registry;
using DeviceGateway.Server.Sessions;
using DeviceGateway.Shared.Contracts;
using DeviceGateway.Shared.Protocol;
using SuperSocket.Command;
using SuperSocket.Server.Abstractions.Session;

namespace DeviceGateway.Server.Commands;

/// <summary>
/// 处理 0x01 心跳消息。
/// <para>
/// 心跳是设备在线状态的唯一依据 —— 高频的位置数据不刷新心跳时间，
/// 否则数据一多就掩盖了心跳缺失，判不出真正的离线。
/// </para>
/// </summary>
[Command("Heartbeat", Key = (byte)DeviceMessageTypes.Heartbeat)]
public class HeartbeatCommand : IAsyncCommand<DeviceSession, DevicePackage>
{
    /// <summary>
    /// 设备注册表。
    /// </summary>
    private readonly DeviceRegistry _registry;

    /// <summary>
    /// 构造心跳命令。
    /// </summary>
    /// <param name="registry">设备注册表。</param>
    public HeartbeatCommand(DeviceRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// 执行心跳处理：登记设备（首次上报则自动添加）并刷新心跳时间。
    /// </summary>
    /// <param name="session">设备会话。</param>
    /// <param name="package">消息包。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async ValueTask ExecuteAsync(DeviceSession session, DevicePackage package, CancellationToken cancellationToken)
    {
        if (!DevicePayloadReader.TryReadDeviceId(package, out var deviceId))
        {
            // 拿不到设备标识的消息没法处理，但也不能因此让连接崩掉。
            session.LogWarning("收到无法解析 deviceId 的心跳消息，已忽略");
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // 校验设备标识、自动登记设备、把会话登记到注册表。
        if (!await _registry.TryBind(session, deviceId, now))
        {
            return;
        }

        // 刷新心跳时间 —— 离线判定只看这个时间。
        _registry.TouchHeartbeat(deviceId,session, now);

        // 记录一条 Debug 日志，便于观察设备心跳是否正常到达。
        session.LogDebug("收到设备 {DeviceId} 的心跳", deviceId);
    }
}
