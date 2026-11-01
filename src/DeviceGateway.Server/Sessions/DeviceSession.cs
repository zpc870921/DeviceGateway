using DeviceGateway.Server.Registry;
using DeviceGateway.Shared.Protocol;
using SuperSocket.Connection;
using SuperSocket.ProtoBase;
using SuperSocket.Server;
using SuperSocket.Server.Abstractions.Session;

namespace DeviceGateway.Server.Sessions;


/// <summary>
/// 设备 TCP 连接会话。
///
/// 职责：
/// 1. 管理设备身份。
/// 2. 管理设备连接生命周期。
/// 3. 提供统一的下行发送入口。
/// 4. 防止同一 Session 的多个发送操作并发执行。
/// 5. 连接断开时注销设备。
/// </summary>
public sealed class DeviceSession : AppSession, ISessionHandle
{
    private readonly IPackageEncoder<DevicePackage> _encoder;

    private readonly DeviceRegistry _registry;

    /// <summary>
    /// 同一 Session 的发送锁。
    ///
    /// 防止多个业务线程同时调用发送方法。
    /// 注意：这是发送操作串行化，不是 TCP 接收缓冲区的锁。
    /// </summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// 设备身份绑定锁。
    /// </summary>
    private readonly object _bindLock = new();

    /// <summary>
    /// 0：连接正常
    /// 1：连接正在关闭或已经关闭
    /// </summary>
    private int _isClosing;

    /// <summary>
    /// 当前绑定的设备编号。
    /// 绑定后不允许修改。
    /// </summary>
    private string _deviceId = string.Empty;

    public string DeviceId => Volatile.Read(ref _deviceId);

    public string SessionId => SessionID;

    public bool IsClosing => Volatile.Read(ref _isClosing) == 1;

    /// <summary>
    /// 本会话的日志器。
    /// <para>
    /// 框架的 <c>AppSession.Logger</c> 依赖挂在会话上的 <c>Server</c>，
    /// 脱离宿主单独用（比如单元测试）时会抛 <see cref="NullReferenceException"/>。
    /// 这里保存一份可注入的日志器，没有注入时回退到框架的会话日志器。
    /// </para>
    /// </summary>
    private readonly ILogger? _logger;

    public DeviceSession(
        IPackageEncoder<DevicePackage> encoder,
        DeviceRegistry registry,
        ILogger<DeviceSession>? logger = null)
    {
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger;
    }

    /// <summary>
    /// 写日志。注入的日志器优先，没有就用框架的会话日志器。
    /// </summary>
    /// <remarks>
    /// 框架的会话日志器在脱离宿主时会抛异常，日志失败不应该影响业务流程，
    /// 所以这里统一兜住。
    /// </remarks>
    private void Log(
        LogLevel level,
        string message,
        params object?[] args)
    {
        try
        {
            if (_logger != null)
            {
                _logger.Log(level, message, args);
                return;
            }

            Logger.Log(level, message, args);
        }
        catch
        {
            // 日志失败不能影响业务流程 —— 直接吞掉。
        }
    }

    /// <summary>
    /// 写一条警告日志。
    /// </summary>
    /// <param name="message">日志模板。</param>
    /// <param name="args">模板参数。</param>
    public void LogWarning(string message, params object?[] args)
        => Log(LogLevel.Warning, message, args);

    /// <summary>
    /// 写一条错误日志。
    /// </summary>
    /// <param name="message">日志模板。</param>
    /// <param name="args">模板参数。</param>
    public void LogError(string message, params object?[] args)
        => Log(LogLevel.Error, message, args);

    /// <summary>
    /// 写一条信息日志。
    /// </summary>
    /// <param name="message">日志模板。</param>
    /// <param name="args">模板参数。</param>
    public void LogInformation(string message, params object?[] args)
        => Log(LogLevel.Information, message, args);

    /// <summary>
    /// 写一条调试日志。
    /// </summary>
    /// <param name="message">日志模板。</param>
    /// <param name="args">模板参数。</param>
    public void LogDebug(string message, params object?[] args)
        => Log(LogLevel.Debug, message, args);

    #region 设备身份绑定

    /// <summary>
    /// 绑定或验证设备身份。
    ///
    /// 第一次调用：绑定设备编号。
    /// 后续调用：验证设备编号是否一致。
    /// </summary>
    public bool TryBindDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        lock (_bindLock)
        {
            if (IsClosing)
                return false;

            // 第一次绑定
            if (string.IsNullOrEmpty(_deviceId))
            {
                Volatile.Write(ref _deviceId, deviceId);
                return true;
            }

            // 后续只能使用相同的设备编号
            return string.Equals(
                _deviceId,
                deviceId,
                StringComparison.Ordinal);
        }
    }

    #endregion

    #region 统一发送入口

    /// <summary>
    /// 向当前设备发送消息。
    ///
    /// 所有服务端下行消息统一从这里发送。
    /// </summary>
    public async Task SendPackageAsync(
        DevicePackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        // 第一次检查，尽早拒绝已经关闭的 Session
        ThrowIfClosing();

        // 获取发送锁
        await _sendLock.WaitAsync(cancellationToken);

        try
        {
            // 第二次检查，避免等待锁期间连接已经关闭
            ThrowIfClosing();

            // 使用 SuperSocket 提供的发送 API
            await ((IAppSession)this).SendAsync(
                _encoder,
                package,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消，继续向上抛出
            throw;
        }
        catch (Exception ex)
        {
            Log(
                LogLevel.Error,
                "[设备会话 {SessionId}] 发送消息失败，DeviceId={DeviceId}，异常={Exception}",
                SessionID,
                DeviceId,
                ex.Message);

            // 不要吞掉异常，让调用方知道发送失败
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 持锁发送 —— <see cref="SendPackageAsync"/> 的可测试形态。
    /// </summary>
    /// <param name="package">要发送的消息包。</param>
    /// <param name="transport">
    /// 实际的传输动作。生产环境传入 socket 发送；
    /// 单元测试传入记录用的假实现，从而能观察字节流是否交错。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 发送必须串行：跨设备消息（HTTP 线程触发）与设备上报的响应可能同时写同一个会话，
    /// 两次并发写会让字节交错，接收方解析出来的帧就坏了。
    /// 这个方法把"持锁"和"怎么发"分开，前者可以被测试验证。
    /// </remarks>
    internal async Task SendSerializedAsync(
        DevicePackage package,
        Func<DevicePackage, CancellationToken, Task> transport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(transport);

        // 第一次检查，尽早拒绝已经关闭的 Session
        ThrowIfClosing();

        // 获取发送锁
        await _sendLock.WaitAsync(cancellationToken);

        try
        {
            // 第二次检查，避免等待锁期间连接已经关闭
            ThrowIfClosing();

            await transport(package, cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消，继续向上抛出
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 判断当前 Session 是否已经关闭。
    /// </summary>
    private void ThrowIfClosing()
    {
        if (IsClosing)
        {
            throw new InvalidOperationException(
                $"设备会话已经关闭，无法发送消息。SessionId={SessionID}");
        }
    }

    #endregion

    #region 连接生命周期

    /// <summary>
    /// 连接建立。
    /// </summary>
    protected override ValueTask OnSessionConnectedAsync()
    {
        Log(
            LogLevel.Information,
            "设备 TCP 连接建立，SessionId={SessionId}",
            SessionID);

        return base.OnSessionConnectedAsync();
    }

    /// <summary>
    /// 连接断开。
    /// </summary>
    protected override async ValueTask OnSessionClosedAsync(
        CloseEventArgs e)
    {
        // 标记关闭，阻止后续发送
        if (Interlocked.Exchange(ref _isClosing, 1) == 1)
        {
            await base.OnSessionClosedAsync(e);
            return;
        }

        try
        {
            UnbindFromRegistry();
        }
        finally
        {
            // 保证 SuperSocket 基类的关闭逻辑执行
            await base.OnSessionClosedAsync(e);
        }
    }

    /// <summary>
    /// 把本会话从注册表注销掉，并把设备标记为离线。
    /// </summary>
    /// <remarks>
    /// 只有当前注册的会话确实是自己时才注销 —— 设备重连之后，
    /// 旧会话的关闭事件不能把新会话的绑定删掉。
    /// <para>
    /// 单独抽出来是为了能脱离真实 socket 做单元测试：
    /// <c>AppSession.CloseAsync()</c> 在没有连接时直接返回，
    /// <c>OnSessionClosedAsync</c> 根本不会触发。
    /// </para>
    /// </remarks>
    internal void UnbindFromRegistry()
    {
        try
        {
            string deviceId = DeviceId;

            if (string.IsNullOrEmpty(deviceId))
            {
                return;
            }

            if (_registry.UnbindAndMarkOffline(deviceId, this))
            {
                Log(
                    LogLevel.Information,
                    "设备 {DeviceId} 已断开，SessionId={SessionId}",
                    deviceId,
                    SessionID);
            }
            else
            {
                Log(
                    LogLevel.Information,
                    "设备 {DeviceId} 的当前绑定已发生变化，跳过旧会话注销，SessionId={SessionId}",
                    deviceId,
                    SessionID);
            }
        }
        catch (Exception ex)
        {
            Log(
                LogLevel.Error,
                "设备会话关闭清理失败，SessionId={SessionId}，异常={Exception}",
                SessionID,
                ex.Message);
        }
    }

    /// <summary>
    /// 主动关闭当前连接。
    /// </summary>
    public override ValueTask CloseAsync()
    {
        Interlocked.Exchange(ref _isClosing, 1);

        return base.CloseAsync(CloseReason.LocalClosing);
    }

    /// <summary>
    /// <see cref="ISessionHandle"/> 的发送实现。
    /// </summary>
    /// <remarks>
    /// 必须走 <see cref="SendPackageAsync"/> —— 那条路径才有发送锁。
    /// 直接调 <c>IAppSession.SendAsync</c> 会绕过锁，
    /// 跨设备消息（HTTP 线程）与设备上报响应并发写同一会话时字节会交错。
    /// </remarks>
    public async Task SendAsync(DevicePackage package, CancellationToken cancellationToken)
    {
        await SendPackageAsync(package, cancellationToken);
    }

    #endregion
}
