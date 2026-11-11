using System.Globalization;
using System.Text.Json;
using Horica.ClaimsService.Common;

namespace Horica.ClaimsService.Infrastructure.Messaging;

/// <summary>
/// Lenient readers for incoming JSON: property names are case-insensitive, several alternative names can be
/// given, and ids may be strings or numbers. Keeps this service working even if another service differs
/// slightly from the documented contract.
/// </summary>
public static class JsonRead
{
    public static JsonDocument ParseDocument(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidMessageException("Message body is empty.");
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidMessageException("Message is not valid JSON: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Accepts a CloudEvents envelope ({ "specversion": "1.0", "id": ..., "data": { ... } }) or the bare data object.
    /// Returns the event id (the envelope id, else the fallback) and the data object.
    /// </summary>
    public static (string EventId, JsonElement Data) Unwrap(JsonElement root, string fallbackEventId)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidMessageException("Message root must be a JSON object.");
        }

        var eventId = fallbackEventId;
        var payload = root;

        if (TryGetProperty(root, "specversion", out _))
        {
            var envelopeId = ReadString(root, "id");
            if (!string.IsNullOrWhiteSpace(envelopeId))
            {
                eventId = envelopeId;
            }

            if (!TryGetProperty(root, "data", out payload) || payload.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidMessageException("CloudEvent does not contain a 'data' object.");
            }
        }

        return (TextUtil.Truncate(eventId, 300), payload);
    }

    public static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>The first of the names that is present and not null.</summary>
    public static bool TryGetAny(JsonElement obj, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(obj, name, out value) && value.ValueKind != JsonValueKind.Null)
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    public static string? ScalarToString(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.GetRawText();
            default:
                return null;
        }
    }

    public static string? ReadString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(obj, name, out var element))
            {
                var text = ScalarToString(element);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }
        }

        return null;
    }

    public static string? ReadString(JsonElement obj, int maxLength, params string[] names)
    {
        var value = ReadString(obj, names);
        return value is null ? null : TextUtil.Truncate(value, maxLength);
    }

    public static double? ReadDouble(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(obj, name, out var element) || element.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
            {
                return number;
            }

            if (element.ValueKind == JsonValueKind.String &&
                double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            throw new InvalidMessageException("Field '" + name + "' is not a valid number.");
        }

        return null;
    }

    public static int? ReadInt(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(obj, name, out var element) || element.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
            {
                return number;
            }

            if (element.ValueKind == JsonValueKind.String &&
                int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            throw new InvalidMessageException("Field '" + name + "' is not a valid whole number.");
        }

        return null;
    }

    public static bool? ReadBool(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(obj, name, out var element))
            {
                continue;
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.String when bool.TryParse(element.GetString(), out var parsed):
                    return parsed;
            }
        }

        return null;
    }

    public static DateTimeOffset? ReadTime(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(obj, name, out var element) || element.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (DateTimeOffset.TryParse(
                    element.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                // PostgreSQL (timestamptz) only accepts UTC values.
                return parsed.ToUniversalTime();
            }
        }

        return null;
    }

    /// <summary>An array of strings, or a single string.</summary>
    public static List<string> ReadStringList(JsonElement obj, int maxItems, int maxLength, params string[] names)
    {
        var result = new List<string>();

        foreach (var name in names)
        {
            if (!TryGetProperty(obj, name, out var element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    var text = ScalarToString(item);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        result.Add(TextUtil.Truncate(text.Trim(), maxLength));
                    }

                    if (result.Count >= maxItems)
                    {
                        break;
                    }
                }
            }
            else
            {
                var text = ScalarToString(element);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(TextUtil.Truncate(text.Trim(), maxLength));
                }
            }

            if (result.Count > 0)
            {
                break;
            }
        }

        return result;
    }
}
