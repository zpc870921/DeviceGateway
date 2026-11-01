using System.Text.Json;

namespace DeviceGateway.Shared.Contracts;

/// <summary>
/// 项目统一的 JSON 序列化配置。
/// <para>
/// 沿用仓库既有约定（<c>supersocketdemo/Models/DefaultEncoder.cs</c>）：
/// 不做 HTML 转义，避免中文被转成 <c>\uXXXX</c> 这种不可读的转义序列。
/// 服务端与客户端共用同一份配置，保证两边序列化出来的 JSON 完全一致。
/// </para>
/// </summary>
public static class DeviceGatewayJson
{
    /// <summary>
    /// 统一的 JSON 序列化选项。
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 把对象序列化成 JSON 文本。
    /// </summary>
    /// <param name="value">待序列化的对象。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// 把 JSON 文本反序列化成对象。解析失败时返回 false 而不是抛异常 ——
    /// 设备发来的数据不可信，不能因为一条坏消息就让整个连接崩掉。
    /// </summary>
    /// <param name="json">JSON 文本。</param>
    /// <param name="value">反序列化结果。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryDeserialize<T>(string json, out T? value)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json, Options);
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }
}
