using DeviceGateway.Server.Registry;
using DeviceGateway.Shared.Contracts;
using DeviceGateway.Shared.Protocol;
using Microsoft.AspNetCore.Mvc;

namespace DeviceGateway.Server.Controllers;

/// <summary>
/// 设备的 HTTP 接口。
/// <para>
/// 路由按仓库既有约定 <c>api/[controller]/[action]</c>，例如
/// <c>GET /api/Device/List</c>、<c>POST /api/Device/SendMessage</c>。
/// </para>
/// </summary>
[ApiController]
[Route("api/[controller]/[action]")]
public class DeviceController : ControllerBase
{
    /// <summary>
    /// 设备注册表。
    /// </summary>
    private readonly DeviceRegistry _registry;

    /// <summary>
    /// 日志。
    /// </summary>
    private readonly ILogger<DeviceController> _logger;

    /// <summary>
    /// 构造设备控制器。
    /// </summary>
    /// <param name="registry">设备注册表。</param>
    /// <param name="logger">日志。</param>
    public DeviceController(DeviceRegistry registry, ILogger<DeviceController> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// 获取设备列表（含在线状态与最后心跳时间）。
    /// </summary>
    /// <returns>设备列表。</returns>
    [HttpGet]
    public IActionResult List()
    {
        var devices = _registry.AllDevices
            .OrderBy(d => d.DeviceId, StringComparer.Ordinal)
            .Select(d => new DeviceInfo
            {
                DeviceId = d.DeviceId,
                Vendor = d.Vendor,
                Status = d.Status.ToString(),
                LastHeartbeatAt = d.LastHeartbeatAt,
                FirstSeenAt = d.FirstSeenAt
            })
            .ToList();

        return Ok(devices);
    }

    /// <summary>
    /// 获取单个设备详情。
    /// </summary>
    /// <param name="deviceId">设备标识。</param>
    /// <returns>设备详情；设备不存在时返回 404。</returns>
    [HttpGet]
    public IActionResult Detail(string deviceId)
    {
        var record = _registry.TryGetDevice(deviceId);
        if (record == null)
        {
            return NotFound(new DeviceNotFoundResponse { DeviceId = deviceId });
        }

        return Ok(new DeviceInfo
        {
            DeviceId = record.DeviceId,
            Vendor = record.Vendor,
            Status = record.Status.ToString(),
            LastHeartbeatAt = record.LastHeartbeatAt,
            FirstSeenAt = record.FirstSeenAt
        });
    }

    /// <summary>
    /// 下发跨设备消息：把消息推送给目标设备的在线连接。
    /// </summary>
    /// <param name="request">消息请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 200 已发送；400 参数非法；404 目标设备不存在；409 目标设备离线；502 发送失败。
    /// </returns>
    [HttpPost]
    public async Task<IActionResult> SendMessage(SendMessageRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.From) || string.IsNullOrWhiteSpace(request.To))
        {
            return BadRequest(new { status = "invalid", message = "from 和 to 都不能为空" });
        }

        if (string.Equals(request.From, request.To, StringComparison.Ordinal))
        {
            return BadRequest(new { status = "invalid", message = "不能给自己发消息" });
        }

        // 目标设备完全没听说过 —— 404
        if (_registry.TryGetDevice(request.To) == null)
        {
            return NotFound(new DeviceNotFoundResponse { DeviceId = request.To });
        }

        // 设备存在但当前不在线 —— 409
        var session = _registry.TryGetSession(request.To);
        if (session == null)
        {
            return Conflict(new DeviceOfflineResponse { DeviceId = request.To });
        }

        // 把负载序列化成 JSON 文本，放进数据帧发给目标设备。
        var payloadJson = DeviceGatewayJson.Serialize(request.Payload ?? new { });

        var package = new DevicePackage
        {
            Key = DeviceMessageTypes.Report,
            Payload = payloadJson
        };

        try
        {
            // 注意：这里是入队而不是直接写 socket ——
            // 直接写会和设备上报响应并发，可能字节交错；
            // 而且慢设备不该把 HTTP 请求拖住。
            await session.SendAsync(package, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "向设备 {DeviceId} 下发消息失败", request.To);

            return Problem(
                title: "发送失败",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Ok(new SendMessageResponse { From = request.From, To = request.To });
    }
}
