using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

internal sealed class ActivitySegmentFactProjector
{
    public Guid ProjectedId(Guid streamId, Guid factId)
    {
        var identity = Encoding.ASCII.GetBytes($"{streamId:D}/{factId:D}");
        var value = (factId.ToString("N")[..12] +
                     Convert.ToHexStringLower(SHA256.HashData(identity).AsSpan(0, 10))).ToCharArray();
        value[12] = '7';
        value[16] = "89ab"[Convert.ToInt32(value[16].ToString(), 16) & 0b11];
        return Guid.ParseExact(new string(value), "N");
    }

    public bool TryProject(
        FactStreamState stream,
        Guid factId,
        DateTimeOffset start,
        DateTimeOffset end,
        JsonElement payload,
        out ActivitySegmentItem? item)
    {
        item = null;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("identityKey", out var identityKey) ||
            identityKey.ValueKind != JsonValueKind.String ||
            identityKey.GetString() is not { } projectedIdentityKey ||
            string.IsNullOrWhiteSpace(projectedIdentityKey))
            return false;

        // Stream 上的通用 appIdentityKey dimension 是 App 身份的权威来源；宿主不解析任何具体
        // Collector 的产品词汇。没有该 dimension 的 Stream（例如 System Collector 自己的输出）退回
        // Fact 自报的 appIdentityKey。Backend/App Catalog 认不认识这个 Key 与投影无关。
        var appIdentityKey =
            stream.Dimensions.TryGetValue("appIdentityKey", out var dimensionAppIdentityKey) &&
            !string.IsNullOrWhiteSpace(dimensionAppIdentityKey)
                ? dimensionAppIdentityKey
                : StringProperty(payload, "appIdentityKey");
        JsonElement? attributes = stream.SchemaId == "heartbeat.system.foreground-segment"
            ? null
            : payload.Clone();

        item = new ActivitySegmentItem
        {
            Id = ProjectedId(stream.StreamId, factId),
            Source = stream.Source,
            IdentityKey = projectedIdentityKey,
            Title = StringProperty(payload, "title"),
            AppIdentityKey = appIdentityKey,
            AppDisplayName = StringProperty(payload, "appDisplayName"),
            StartTime = start,
            EndTime = end,
            Attributes = attributes
        };
        return true;
    }

    private static string? StringProperty(JsonElement payload, string propertyName) =>
        payload.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
