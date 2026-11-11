namespace Horica.ClaimsService.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>"Jwt" (default; validates auth-service / Keycloak tokens) or "Disabled" (Development only).</summary>
    public string Mode { get; set; } = "Jwt";

    /// <summary>Token issuer, e.g. http://auth-service:8081 or https://keycloak.example.com/realms/horica</summary>
    public string? Authority { get; set; }

    /// <summary>Optional. When set, tokens must contain this audience.</summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Optional. Use when the service reaches the issuer at a different URL than the one in the token
    /// (e.g. inside Docker). Example: http://keycloak:8080/realms/horica/.well-known/openid-configuration
    /// </summary>
    public string? MetadataAddress { get; set; }

    /// <summary>Optional. Overrides the expected token issuer (the "iss" claim).</summary>
    public string? ValidIssuer { get; set; }

    /// <summary>Optional. Keycloak client id whose client roles (resource_access) should also be read.</summary>
    public string? ClientId { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Comma separated roles of customers (submit claims, see their own claims). Case-insensitive.</summary>
    public string CustomerRoles { get; set; } = "CLIENT,CUSTOMER";

    /// <summary>Comma separated roles of employees (see every claim). Case-insensitive.</summary>
    public string StaffRoles { get; set; } = "AGENT,REVIEWER,ADMIN,EMPLOYEE,MANAGER";

    /// <summary>Comma separated roles that may read the audit log of all claims (UC-8). Case-insensitive.</summary>
    public string ManagerRoles { get; set; } = "ADMIN,MANAGER";

    public bool IsDisabled => string.Equals(Mode, "Disabled", StringComparison.OrdinalIgnoreCase);

    public IReadOnlySet<string> CustomerRoleSet => Split(CustomerRoles);

    public IReadOnlySet<string> StaffRoleSet => Split(StaffRoles);

    public IReadOnlySet<string> ManagerRoleSet => Split(ManagerRoles);

    private static IReadOnlySet<string> Split(string? value)
    {
        return new HashSet<string>(
            (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }
}
