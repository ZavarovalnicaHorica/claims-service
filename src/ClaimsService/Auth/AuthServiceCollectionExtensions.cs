using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace Horica.ClaimsService.Auth;

public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddClaimsAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));

        var auth = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();

        if (auth.IsDisabled)
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "Auth:Mode=Disabled is only allowed when ASPNETCORE_ENVIRONMENT=Development. " +
                    "Configure Auth:Authority (auth-service or Keycloak realm URL) for any other environment.");
            }

            services
                .AddAuthentication(DevAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });
        }
        else
        {
            if (string.IsNullOrWhiteSpace(auth.Authority))
            {
                throw new InvalidOperationException(
                    "Auth:Authority is not configured. Set it to the token issuer, e.g. http://auth-service:8081 " +
                    "(env var Auth__Authority), or use Auth__Mode=Disabled in Development.");
            }

            services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Authority = auth.Authority;
                    if (!string.IsNullOrWhiteSpace(auth.MetadataAddress))
                    {
                        options.MetadataAddress = auth.MetadataAddress;
                    }

                    options.RequireHttpsMetadata = auth.RequireHttpsMetadata;
                    options.MapInboundClaims = false;

                    var parameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = !string.IsNullOrWhiteSpace(auth.Audience),
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromSeconds(30),
                        NameClaimType = "preferred_username",
                        RoleClaimType = ClaimTypes.Role
                    };

                    if (!string.IsNullOrWhiteSpace(auth.Audience))
                    {
                        parameters.ValidAudience = auth.Audience;
                    }

                    if (!string.IsNullOrWhiteSpace(auth.ValidIssuer))
                    {
                        parameters.ValidIssuer = auth.ValidIssuer;
                    }

                    options.TokenValidationParameters = parameters;

                    options.Events = new JwtBearerEvents
                    {
                        OnTokenValidated = context =>
                        {
                            if (context.Principal is not null)
                            {
                                KeycloakRoleMapper.AddRoleClaims(context.Principal, auth.ClientId);
                            }

                            return Task.CompletedTask;
                        }
                    };
                });
        }

        // Role names are compared case-insensitively, so "CLIENT" (auth-service) and "client" (Keycloak) both work.
        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.ClaimsUser, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => HasAnyRole(context, auth.CustomerRoleSet, auth.StaffRoleSet)));

            options.AddPolicy(AuthPolicies.Customer, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => HasAnyRole(context, auth.CustomerRoleSet)));

            options.AddPolicy(AuthPolicies.Staff, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => HasAnyRole(context, auth.StaffRoleSet)));

            options.AddPolicy(AuthPolicies.Manager, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => HasAnyRole(context, auth.ManagerRoleSet)));
        });

        return services;
    }

    private static bool HasAnyRole(AuthorizationHandlerContext context, params IReadOnlySet<string>[] allowed)
    {
        var roles = CurrentUserExtensions.RolesOf(context.User);
        return allowed.Any(set => roles.Overlaps(set));
    }
}
