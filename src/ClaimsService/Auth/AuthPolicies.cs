namespace Horica.ClaimsService.Auth;

public static class AuthPolicies
{
    /// <summary>A customer or an employee.</summary>
    public const string ClaimsUser = "ClaimsUser";

    /// <summary>A customer (may submit claims).</summary>
    public const string Customer = "Customer";

    /// <summary>An employee (sees every claim).</summary>
    public const string Staff = "Staff";

    /// <summary>A manager or administrator (audit log of all claims).</summary>
    public const string Manager = "Manager";
}
