using System.Security.Claims;

namespace TodoApi.Authorization;

/// <summary>The claims contract with the Spring Boot identity service (see README).</summary>
public static class Claims
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Roles = "roles";
}

public static class Roles
{
    public const string User = "USER";
    public const string Admin = "ADMIN";
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>The authenticated user's immutable id (JWT <c>sub</c>). Validated at authentication time.</summary>
    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirst(Claims.Subject)?.Value
                   ?? throw new InvalidOperationException("Authenticated principal without subject"));
}
