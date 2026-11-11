using System.Security.Claims;
using System.Text.Json;

namespace Horica.ClaimsService.Auth;

/// <summary>
/// Turns the roles of a token into normal role claims. Two token formats are understood:
///   auth-service: "roles": ["CLIENT"]
///   Keycloak:     realm_access = {"roles":[...]} and resource_access = {"client-id":{"roles":[...]}}
/// </summary>
public static class KeycloakRoleMapper
{
    public static void AddRoleClaims(ClaimsPrincipal principal, string? clientId)
    {
        if (principal.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        var roles = new HashSet<string>(
            ExtractRoles(
                identity.FindFirst("realm_access")?.Value,
                identity.FindFirst("resource_access")?.Value,
                clientId),
            StringComparer.OrdinalIgnoreCase);

        // auth-service tokens use a plain "roles" claim: "roles": ["CLIENT"].
        foreach (var claim in identity.FindAll("roles"))
        {
            if (!string.IsNullOrWhiteSpace(claim.Value))
            {
                roles.Add(claim.Value.Trim());
            }
        }

        foreach (var role in roles)
        {
            if (!identity.HasClaim(ClaimTypes.Role, role))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }
        }
    }

    public static IReadOnlyCollection<string> ExtractRoles(string? realmAccessJson, string? resourceAccessJson, string? clientId)
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectRoles(realmAccessJson, null, roles);

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            CollectRoles(resourceAccessJson, clientId, roles);
        }

        return roles;
    }

    // When clientName is null the JSON is {"roles":[...]}, otherwise {"<clientName>":{"roles":[...]}}.
    private static void CollectRoles(string? json, string? clientName, HashSet<string> roles)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var element = document.RootElement;

            if (clientName is not null)
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(clientName, out var clientElement))
                {
                    return;
                }

                element = clientElement;
            }

            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("roles", out var rolesElement) ||
                rolesElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in rolesElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var role = item.GetString();
                    if (!string.IsNullOrWhiteSpace(role))
                    {
                        roles.Add(role);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Malformed claim: ignore it, the user simply gets no roles from it.
        }
    }
}
