using Lifestream.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Lifestream.Api.Auth;

public static class PolicyNames
{
    /// <summary>Any signed-in caller (Admin, Staff or the demo account).</summary>
    public const string Staff = "Staff";

    /// <summary>Admin only. The demo account is a Staff role, so it never passes.</summary>
    public const string Admin = "Admin";

    /// <summary>May read staff management: Admin, or the demo account (read-only).</summary>
    public const string StaffReader = "StaffReader";

    /// <summary>Only the shared demo account (the "Reset demo data" action).</summary>
    public const string DemoOnly = "DemoOnly";

    /// <summary>Anyone except the demo account (own profile and password writes).</summary>
    public const string NotDemo = "NotDemo";

    public static void Register(AuthorizationOptions options)
    {
        options.AddPolicy(Staff, p => p.RequireAuthenticatedUser());
        options.AddPolicy(Admin, p => p.RequireAuthenticatedUser().RequireAssertion(c => c.User.HasClaim(Claims.Role, "Admin") && !c.User.HasClaim(Claims.Demo, "1")));
        options.AddPolicy(StaffReader, p => p.RequireAuthenticatedUser().RequireAssertion(c => c.User.HasClaim(Claims.Demo, "1") || c.User.HasClaim(Claims.Role, "Admin")));
        options.AddPolicy(DemoOnly, p => p.RequireAuthenticatedUser().RequireAssertion(c => c.User.HasClaim(Claims.Demo, "1")));
        options.AddPolicy(NotDemo, p => p.RequireAuthenticatedUser().RequireAssertion(c => !c.User.HasClaim(Claims.Demo, "1")));
    }
}

/// <summary>Turns a failed policy into a problem response: 403 with code demo_forbidden for the demo account, forbidden for everyone else; 401 stays the default challenge.</summary>
public sealed class ProblemAuthorizationHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            if (context.User.HasClaim(Claims.Demo, "1"))
            {
                await ProblemWriter.WriteAsync(context, StatusCodes.Status403Forbidden, Lifestream.Contracts.ProblemCodes.DemoForbidden, "The demo account cannot do this.");
            }
            else
            {
                await ProblemWriter.WriteAsync(context, StatusCodes.Status403Forbidden, ExtraProblemCodes.Forbidden, "You do not have permission to do this.");
            }

            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
