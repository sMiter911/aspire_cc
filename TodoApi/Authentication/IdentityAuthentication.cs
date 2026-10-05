using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using TodoApi.Authorization;
using TodoApi.Middleware;

namespace TodoApi.Authentication;

/// <summary>Where and how to validate tokens issued by the Spring Boot identity service.</summary>
public class IdentityOptions
{
    public const string Section = "Auth";

    /// <summary>Expected <c>iss</c>. Must equal the Spring service's app.jwt.issuer.</summary>
    public string Issuer { get; set; } = "auth-api";

    /// <summary>This API's audience: tokens must list it in <c>aud</c> (Spring adds it to every access token).</summary>
    public string Audience { get; set; } = "todo-api";

    /// <summary>OpenID discovery URL of the identity service (issuer + jwks_uri). Set by Aspire.</summary>
    public string? MetadataAddress { get; set; }

    /// <summary>Allow plain-HTTP metadata. True only for local development.</summary>
    public bool AllowInsecureMetadata { get; set; }

    /// <summary>Minimum seconds between key refreshes triggered by an unknown <c>kid</c> (key rotation).</summary>
    public int MetadataRefreshSeconds { get; set; } = 300;
}

public static class IdentityAuthentication
{
    /// <summary>
    /// Registers JWT bearer validation. This service is a pure resource API: it never issues tokens, owns no
    /// users, and trusts a token only after signature (RS256, key fetched from the identity service JWKS), issuer,
    /// audience, lifetime and required-claim validation.
    /// </summary>
    public static IServiceCollection AddIdentityAuthentication(this IServiceCollection services, IConfiguration config)
    {
        var opts = config.GetSection(IdentityOptions.Section).Get<IdentityOptions>() ?? new IdentityOptions();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false; // keep claim names exactly as issued: sub, email, roles
                o.MetadataAddress = opts.MetadataAddress;
                o.RequireHttpsMetadata = !opts.AllowInsecureMetadata;
                o.RefreshInterval = TimeSpan.FromSeconds(opts.MetadataRefreshSeconds);
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = opts.Issuer,
                    ValidateAudience = true,
                    ValidAudience = opts.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.Zero,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256], // blocks alg=none and HS256 key-confusion
                    NameClaimType = Claims.Subject,
                    RoleClaimType = Claims.Roles,
                };
                o.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ctx =>
                    {
                        // Ownership hangs off 'sub'. A token without a valid user id is useless: refuse it.
                        if (!Guid.TryParse(ctx.Principal?.FindFirst(Claims.Subject)?.Value, out _))
                        {
                            ctx.Fail("Missing or invalid subject");
                        }
                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = ctx =>
                    {
                        // Log the failure class only: never the token or Authorization header.
                        ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Authentication")
                            .LogWarning("Token rejected ({Reason}) for {Method} {Path}",
                                ctx.Exception.GetType().Name, ctx.Request.Method, ctx.Request.Path);
                        return Task.CompletedTask;
                    },
                    OnChallenge = async ctx =>
                    {
                        ctx.HandleResponse();
                        ctx.Response.StatusCode = 401;
                        ctx.Response.Headers.WWWAuthenticate = "Bearer";
                        await ctx.Response.WriteAsJsonAsync(ApiError.ForStatus(401, ctx.Request.Path));
                    },
                    OnForbidden = async ctx =>
                    {
                        ctx.Response.StatusCode = 403;
                        await ctx.Response.WriteAsJsonAsync(ApiError.ForStatus(403, ctx.Request.Path));
                    },
                };
            });

        return services;
    }
}
