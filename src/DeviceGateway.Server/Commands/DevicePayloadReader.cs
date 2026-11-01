using System.Text.Json;
using DeviceGateway.Shared.Protocol;

namespace DeviceGateway.Server.Commands;

/// <summary>
/// 从消息负载里读取设备标识的公共逻辑。
/// <para>
/// 设备标识放在 JSON 的 <c>deviceId</c> 字段里，所有消息类型都有这个字段。
/// 解析失败一律返回 false 而不是抛异常 —— 设备发来的数据不可信，
/// 不能因为一条坏消息就让整条连接断掉。
/// </para>
/// </summary>
public static class DevicePayloadReader
{
    /// <summary>
    /// 从消息负载里读出 deviceId。
    /// </summary>
    /// <param name="package">消息包。</param>
    /// <param name="deviceId">读出的设备标识。</param>
    /// <returns>读到了合法的 deviceId 返回 true。</returns>
    public static bool TryReadDeviceId(DevicePackage package, out string deviceId)
    {
        deviceId = string.Empty;

        if (string.IsNullOrWhiteSpace(package.Payload))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(package.Payload);

            if (!doc.RootElement.TryGetProperty("deviceId", out var element))
            {
                return false;
            }

            if (element.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = element.GetString();

            // deviceId 是设备的唯一标识，空值或超长都视为非法。
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            {
                return false;
            }

            deviceId = value;
            return true;
        }
        catch (JsonException)
        {
            // 负载不是合法 JSON —— 当作坏消息处理，不抛异常。
            return false;
        }
    }
}
