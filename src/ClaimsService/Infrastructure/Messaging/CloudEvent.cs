using System.Text.Json.Serialization;

namespace Horica.ClaimsService.Infrastructure.Messaging;

/// <summary>CloudEvents 1.0 envelope (JSON structured mode).</summary>
public sealed class CloudEvent<TData>
{
    [JsonPropertyName("specversion")]
    public string SpecVersion { get; init; } = "1.0";

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("subject")]
    public string? Subject { get; init; }

    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; init; }

    [JsonPropertyName("datacontenttype")]
    public string DataContentType { get; init; } = "application/json";

    [JsonPropertyName("data")]
    public TData Data { get; init; } = default!;
}
