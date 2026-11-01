using DeviceGateway.Server.Registry;
using DeviceGateway.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace DeviceGateway.Server.Background;

/// <summary>
/// 网关自身的业务配置。
/// </summary>
public class DeviceGatewayOptions
{
    /// <summary>
    /// 心跳超时时长。设备 30 秒一次心跳，默认按 3 个周期（90 秒）判离线。
    /// </summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// 离线扫描间隔。10 秒扫一次，实际判离线延迟在 90~100 秒之间。
    /// </summary>
    public int OfflineScanIntervalSeconds { get; set; } = 10;
}

/// <summary>
/// 离线看门狗：按心跳超时把设备标记为离线。
/// <para>
/// <b>为什么不用 SuperSocket 自带的 <c>UseClearIdleSession</c>：</b>
/// 它判定依据是 <c>LastActiveTime</c>，而这个值在每次 socket 读写成功时都会刷新 ——
/// 它表示"连接有 I/O"，不是"设备还活着"。本项目的设备每秒发一包位置数据，
/// 那个超时永远不会触发，心跳停了但连接还在的设备会一直挂着。
/// </para>
/// <para>
/// 所以业务在线状态由这个看门狗判定，自带的机制只作为回收僵死 socket 的兜底（另一层）。
/// </para>
/// </summary>
public class DeviceHeartbeatMonitor : BackgroundService
{
    /// <summary>
    /// 设备注册表。
    /// </summary>
    private readonly DeviceRegistry _registry;

    /// <summary>
    /// 网关配置。
    /// </summary>
    private readonly DeviceGatewayOptions _options;

    /// <summary>
    /// 日志。
    /// </summary>
    private readonly ILogger<DeviceHeartbeatMonitor> _logger;

    /// <summary>
    /// 构造看门狗。
    /// </summary>
    /// <param name="registry">设备注册表。</param>
    /// <param name="options">网关配置。</param>
    /// <param name="logger">日志。</param>
    public DeviceHeartbeatMonitor(DeviceRegistry registry, IOptions<DeviceGatewayOptions> options, ILogger<DeviceHeartbeatMonitor> logger)
    {
        _registry = registry;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 周期性扫描并把心跳超时的设备标记为离线。
    /// </summary>
    /// <param name="stoppingToken">停止令牌。</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeout = TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds);
        var interval = TimeSpan.FromSeconds(_options.OfflineScanIntervalSeconds);

        _logger.LogInformation(
            "离线看门狗已启动：心跳超时 {TimeoutSeconds} 秒，扫描间隔 {IntervalSeconds} 秒",
            _options.HeartbeatTimeoutSeconds, _options.OfflineScanIntervalSeconds);

        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckHeartbeatAsync(timeout,stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "设备心跳检测发生异常");
            }
        }
    }

    private async Task CheckHeartbeatAsync(TimeSpan timeout,CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var expiredSessions = _registry.ExpireHeartbeatSessions(now,timeout);

        if (expiredSessions.Count == 0)
            return;

        _logger.LogWarning(
            "检测到 {Count} 个设备心跳超时",
            expiredSessions.Count);

        foreach (var session in expiredSessions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await session.CloseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "关闭心跳超时设备连接失败");
            }
        }
    }
}
