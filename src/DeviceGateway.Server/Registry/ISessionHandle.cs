using DeviceGateway.Shared.Protocol;

namespace DeviceGateway.Server.Registry;

/// <summary>
/// 一个在线设备连接的抽象。
/// <para>
/// 注册表只依赖这个接口，不直接依赖 SuperSocket 的 <c>IAppSession</c>。
/// 这样做有两个好处：一是注册表可以脱离 SuperSocket 单独写单元测试；
/// 二是把"发送"这件事统一收敛到一个出口，避免多处并发写同一个会话导致字节交错。
/// </para>
/// </summary>
public interface ISessionHandle
{
    /// <summary>
    /// 会话标识，用于判断"是不是同一个会话"。
    /// </summary>
    string SessionId { get; }

    /// <summary>
    /// 绑定的设备标识。
    /// </summary>
    string DeviceId { get; }

    /// <summary>
    /// 把一个消息包发送给设备。
    /// <para>
    /// 实现必须是<b>串行</b>的：同一会话上的多次发送不能字节交错。
    /// </para>
    /// </summary>
    /// <param name="package">要发送的消息包。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    Task SendAsync(DevicePackage package, CancellationToken cancellationToken);

    /// <summary>
    /// 主动关闭连接。
    /// </summary>
    ValueTask CloseAsync();
}
