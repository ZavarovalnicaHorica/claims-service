using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horica.ClaimsService.Infrastructure.Messaging;

public static class JsonDefaults
{
    /// <summary>
    /// camelCase, enums as UPPER_SNAKE strings (NEEDS_REVIEW, APPROVE ...), nulls omitted.
    /// Used for all events written by this service and for the REST API, so both look the same.
    /// </summary>
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        return options;
    }
}
