using System.Security.Claims;
using Horica.ClaimsService.Auth;
using Horica.ClaimsService.Common;
using Horica.ClaimsService.Infrastructure.Policies;
using Xunit;

namespace Horica.ClaimsService.Tests;

public class AuthTests
{
    private static readonly AuthOptions Options = new AuthOptions();

    /// <summary>A principal as JwtBearer builds it from an auth-service token: sub, email, roles.</summary>
    private static ClaimsPrincipal AuthServiceToken(string role)
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim("sub", "7f1c0f9e-0000-0000-0000-000000000001"),
                new Claim("email", "ana@horica.test"),
                new Claim("roles", role)
            },
            "Bearer",
            "preferred_username",
            ClaimTypes.Role);

        var principal = new ClaimsPrincipal(identity);
        KeycloakRoleMapper.AddRoleClaims(principal, null);
        return principal;
    }

    [Fact]
    public void A_client_of_auth_service_is_a_customer()
    {
        var user = AuthServiceToken("CLIENT").ToCurrentUser(Options);

        Assert.Equal("7f1c0f9e-0000-0000-0000-000000000001", user.Id);
        Assert.Equal("ana", user.DisplayName);
        Assert.Equal("ana@horica.test", user.Email);
        Assert.True(user.IsCustomer);
        Assert.False(user.IsStaff);
        Assert.False(user.IsManager);
    }

    [Theory]
    [InlineData("REVIEWER", true, false)]
    [InlineData("AGENT", true, false)]
    [InlineData("ADMIN", true, true)]
    [InlineData("reviewer", true, false)]
    [InlineData("admin", true, true)]
    public void Staff_roles_are_compared_case_insensitively(string role, bool staff, bool manager)
    {
        var user = AuthServiceToken(role).ToCurrentUser(Options);

        Assert.False(user.IsCustomer);
        Assert.Equal(staff, user.IsStaff);
        Assert.Equal(manager, user.IsManager);
    }

    [Fact]
    public void Keycloak_realm_roles_are_read()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim("sub", "u-1"), new Claim("realm_access", """{"roles":["customer","offline_access"]}""") },
            "Bearer",
            "preferred_username",
            ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);

        KeycloakRoleMapper.AddRoleClaims(principal, null);
        var user = principal.ToCurrentUser(Options);

        Assert.True(user.IsCustomer);
        Assert.False(user.IsStaff);
    }

    [Fact]
    public void Role_lists_can_be_changed_in_configuration()
    {
        var options = new AuthOptions { CustomerRoles = " policyholder , client", StaffRoles = "clerk" };

        Assert.Contains("POLICYHOLDER", options.CustomerRoleSet);
        Assert.Contains("client", options.CustomerRoleSet);
        Assert.Single(options.StaffRoleSet);
    }

    [Theory]
    [InlineData(1200, "1.200 €")]
    [InlineData(300, "300 €")]
    [InlineData(1200.5, "1.200,5 €")]
    [InlineData(18000.25, "18.000,25 €")]
    [InlineData(0.99, "0,99 €")]
    public void Amounts_are_written_like_the_web_ui_writes_them(decimal amount, string expected)
    {
        Assert.Equal(expected, TextUtil.Euro(amount));
    }

    [Fact]
    public void Reads_a_policy_from_policy_service()
    {
        var policy = PolicyClient.Parse(
            """{ "number": "POL-1001", "holderId": "c-ana", "type": "car", "limit": 20000 }""",
            "POL-1001");

        Assert.NotNull(policy);
        Assert.Equal("POL-1001", policy!.Number);
        Assert.Equal("c-ana", policy.HolderId);
        Assert.Equal("CAR", policy.Type);
    }

    [Fact]
    public void Finds_the_policy_in_a_list()
    {
        var policy = PolicyClient.Parse(
            """[{ "number": "POL-1", "type": "HOME" }, { "policyNumber": "POL-2", "type": "TRAVEL" }]""",
            "pol-2");

        Assert.Equal("TRAVEL", policy!.Type);
        Assert.Null(PolicyClient.Parse("""[{ "number": "POL-1" }]""", "POL-9"));
    }
}
