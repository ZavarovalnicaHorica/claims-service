using System.Security.Claims;

namespace Horica.ClaimsService.Auth;

/// <summary>The person performing the current request.</summary>
public sealed record CurrentUser(
    string Id,
    string DisplayName,
    string? Email,
    bool IsCustomer,
    bool IsStaff,
    bool IsManager);

public static class CurrentUserExtensions
{
    public static CurrentUser ToCurrentUser(this ClaimsPrincipal principal, AuthOptions options)
    {
        var id = FirstValue(principal, "sub")
                 ?? FirstValue(principal, ClaimTypes.NameIdentifier)
                 ?? principal.Identity?.Name;

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("The authenticated user has no identifier ('sub') claim.");
        }

        var email = FirstValue(principal, "email") ?? FirstValue(principal, ClaimTypes.Email);

        // auth-service tokens carry only sub + email, so the part before "@" is used as the name,
        // exactly like the web UI does.
        var displayName = FirstValue(principal, "name")
                          ?? FirstValue(principal, "preferred_username")
                          ?? (email is null ? null : EmailLocalPart(email))
                          ?? principal.Identity?.Name
                          ?? id;

        var roles = RolesOf(principal);

        return new CurrentUser(
            Id: id,
            DisplayName: displayName,
            Email: email,
            IsCustomer: roles.Overlaps(options.CustomerRoleSet),
            IsStaff: roles.Overlaps(options.StaffRoleSet),
            IsManager: roles.Overlaps(options.ManagerRoleSet));
    }

    /// <summary>All role claims of the user, compared case-insensitively ("REVIEWER" == "reviewer").</summary>
    public static HashSet<string> RolesOf(ClaimsPrincipal principal)
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var identity in principal.Identities)
        {
            foreach (var claim in identity.FindAll(identity.RoleClaimType))
            {
                if (!string.IsNullOrWhiteSpace(claim.Value))
                {
                    roles.Add(claim.Value.Trim());
                }
            }

            if (identity.RoleClaimType != ClaimTypes.Role)
            {
                foreach (var claim in identity.FindAll(ClaimTypes.Role))
                {
                    if (!string.IsNullOrWhiteSpace(claim.Value))
                    {
                        roles.Add(claim.Value.Trim());
                    }
                }
            }
        }

        return roles;
    }

    private static string EmailLocalPart(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 ? email.Substring(0, at) : email;
    }

    private static string? FirstValue(ClaimsPrincipal principal, string claimType)
    {
        var value = principal.FindFirst(claimType)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
