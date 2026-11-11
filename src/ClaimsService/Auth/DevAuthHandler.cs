using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Horica.ClaimsService.Auth;

/// <summary>
/// Development-only authentication (Auth:Mode = Disabled). Every request is signed in.
/// Headers to pretend to be somebody else:
///   X-Dev-User   user id (default "dev-user")
///   X-Dev-Roles  comma separated roles (default "CLIENT,ADMIN": a customer who is also a manager)
///   X-Dev-Email  optional e-mail (the part before "@" becomes the display name)
/// </summary>
public sealed class DevAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DevAuth";

    public const string DefaultUser = "dev-user";

    public const string DefaultRoles = "CLIENT,ADMIN";

    public DevAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = ReadHeader("X-Dev-User") ?? DefaultUser;
        var rolesHeader = ReadHeader("X-Dev-Roles") ?? DefaultRoles;
        var email = ReadHeader("X-Dev-Email");

        var claims = new List<Claim>
        {
            new Claim("sub", userId),
            new Claim(ClaimTypes.Name, userId)
        };

        if (email is not null)
        {
            // Like an auth-service token: sub + email, the name is the part before "@".
            claims.Add(new Claim("email", email));
        }
        else
        {
            claims.Add(new Claim("preferred_username", userId));
        }

        foreach (var role in rolesHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private string? ReadHeader(string name)
    {
        var value = Request.Headers[name].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
