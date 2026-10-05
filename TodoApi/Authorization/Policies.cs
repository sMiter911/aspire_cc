using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using TodoApi.Models;

namespace TodoApi.Authorization;

/// <summary>
/// Named policies. "Own" policies say "a signed-in USER or ADMIN may attempt this"; whether the attempt succeeds for
/// a particular todo is decided by <see cref="TodoOwnershipHandler"/>. "All" policies guard cross-user access.
/// Policies are the extension point: later they can require a permission claim instead of (or as well as) a role.
/// </summary>
public static class Policies
{
    public const string CreateOwn = "Todo.CreateOwn";
    public const string ReadOwn = "Todo.ReadOwn";
    public const string UpdateOwn = "Todo.UpdateOwn";
    public const string DeleteOwn = "Todo.DeleteOwn";

    public const string ReadAll = "Todo.ReadAll";
    public const string UpdateAll = "Todo.UpdateAll";
    public const string DeleteAll = "Todo.DeleteAll";

    /// <summary>Manually release a waiting todo / retry a failed one (operational actions, ADMIN only).</summary>
    public const string Release = "Todo.Release";
    public const string Retry = "Todo.Retry";

    public static IServiceCollection AddTodoAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            // Deny by default: any endpoint without an explicit [AllowAnonymous] needs a signed-in user.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(CreateOwn, p => p.RequireAuthenticatedUser().RequireRole(Roles.User, Roles.Admin))
            .AddPolicy(ReadOwn, p => p.RequireAuthenticatedUser().RequireRole(Roles.User, Roles.Admin))
            .AddPolicy(UpdateOwn, p => p.RequireAuthenticatedUser().RequireRole(Roles.User, Roles.Admin))
            .AddPolicy(DeleteOwn, p => p.RequireAuthenticatedUser().RequireRole(Roles.User, Roles.Admin))
            .AddPolicy(ReadAll, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin))
            .AddPolicy(UpdateAll, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin))
            .AddPolicy(DeleteAll, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin))
            .AddPolicy(Release, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin))
            .AddPolicy(Retry, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin));

        services.AddSingleton<IAuthorizationHandler, TodoOwnershipHandler>();
        return services;
    }
}

/// <summary>Operations evaluated against a concrete todo.</summary>
public static class TodoOperations
{
    public static readonly OperationAuthorizationRequirement Read = new() { Name = nameof(Read) };
    public static readonly OperationAuthorizationRequirement Update = new() { Name = nameof(Update) };
    public static readonly OperationAuthorizationRequirement Delete = new() { Name = nameof(Delete) };
}

/// <summary>
/// Ownership rule: the owner (<c>Todo.UserId == sub</c>) may act on the todo. Everything else (cross-user access)
/// is NOT granted here; it needs the matching "Todo.*All" policy, which only ADMIN satisfies. TodoService combines
/// the two so there is exactly one place where "can this user touch this todo" is decided.
/// </summary>
public class TodoOwnershipHandler : AuthorizationHandler<OperationAuthorizationRequirement, Todo>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, OperationAuthorizationRequirement requirement, Todo todo)
    {
        if (context.User.Identity?.IsAuthenticated == true && todo.UserId == context.User.GetUserId())
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}
