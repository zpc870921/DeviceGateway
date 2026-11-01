using Microsoft.AspNetCore.Mvc;

namespace DeviceGateway.Server.Controllers;

/// <summary>
/// 健康检查接口。容器编排用它判断实例是否就绪。
/// </summary>
[ApiController]
[Route("api/[controller]/[action]")]
public class HealthController : ControllerBase
{
    /// <summary>
    /// 返回服务状态。
    /// </summary>
    /// <returns>状态信息。</returns>
    [HttpGet]
    public IActionResult Check()
    {
        return Ok(new { status = "ok" });
    }
}
