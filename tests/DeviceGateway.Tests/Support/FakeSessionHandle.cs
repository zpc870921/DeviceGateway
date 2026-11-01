using DeviceGateway.Server.Registry;
using DeviceGateway.Shared.Protocol;

namespace DeviceGateway.Tests.Support;

/// <summary>
/// 测试用的假会话句柄。
/// <para>
/// 注册表只依赖 <see cref="ISessionHandle"/>，不依赖 SuperSocket 的 <c>IAppSession</c>，
/// 所以注册表的单元测试可以用这个假实现，完全不碰 socket。
/// </para>
/// </summary>
internal sealed class FakeSessionHandle : ISessionHandle
{
    /// <summary>
    /// 构造假会话。
    /// </summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="deviceId">绑定的设备标识。</param>
    public FakeSessionHandle(string sessionId, string deviceId = "D1")
    {
        SessionId = sessionId;
        DeviceId = deviceId;
    }

    /// <inheritdoc />
    public string SessionId { get; }

    /// <inheritdoc />
    public string DeviceId { get; }

    /// <summary>收到的消息包。</summary>
    public List<DevicePackage> Sent { get; } = new();

    /// <summary>被关闭的次数。</summary>
    public int CloseCount { get; private set; }

    /// <summary>
    /// 发送时触发的钩子。用于制造并发场景下的延迟。
    /// </summary>
    public Func<DevicePackage, CancellationToken, Task>? OnSend { get; set; }

    /// <summary>
    /// 关闭时触发的钩子。用于模拟"关闭旧连接失败"。
    /// </summary>
    public Func<ValueTask>? OnClose { get; set; }

    /// <summary>
    /// 发送失败时抛出。用于测试发送失败的容错路径。
    /// </summary>
    public Exception? SendException { get; set; }

    /// <inheritdoc />
    public async Task SendAsync(DevicePackage package, CancellationToken cancellationToken)
    {
        if (SendException != null)
        {
            throw SendException;
        }

        if (OnSend != null)
        {
            await OnSend(package, cancellationToken);
        }

        lock (Sent)
        {
            Sent.Add(package);
        }
    }

    /// <inheritdoc />
    public ValueTask CloseAsync()
    {
        CloseCount++;

        if (OnClose != null)
        {
            return OnClose();
        }

        return ValueTask.CompletedTask;
    }
}
