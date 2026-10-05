using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace TodoApi.Middleware;

public static class ErrorHandling
{
    /// <summary>
    /// Everything that fails returns <see cref="ApiError"/>: validation (400), auth (401/403), not found (404) and
    /// unexpected exceptions (opaque 500; details only go to the log).
    /// </summary>
    public static void AddApiErrorHandling(this IServiceCollection services)
    {
        services.Configure<ApiBehaviorOptions>(o =>
        {
            // Let status-code pages produce the envelope for bodiless 404/405/etc. instead of ProblemDetails.
            o.SuppressMapClientErrors = true;
            o.InvalidModelStateResponseFactory = ctx =>
            {
                var errors = ctx.ModelState
                    .Where(kv => kv.Value is { Errors.Count: > 0 })
                    .SelectMany(kv => kv.Value!.Errors.Select(e => new FieldError(
                        Camel(kv.Key.TrimStart('$', '.')),
                        // Never echo parser exception text (can contain type names); use fixed wording for those.
                        string.IsNullOrEmpty(e.ErrorMessage) || e.ErrorMessage.StartsWith("The JSON", StringComparison.Ordinal)
                            || e.ErrorMessage.Contains("could not be converted", StringComparison.Ordinal)
                            ? "Invalid value"
                            : e.ErrorMessage)))
                    .ToList();
                return new BadRequestObjectResult(ApiError.Create(400, "VALIDATION_ERROR",
                    "Request validation failed", ctx.HttpContext.Request.Path, errors));
            };
        });
    }

    public static void UseApiErrorHandling(this WebApplication app)
    {
        app.UseExceptionHandler(builder => builder.Run(async ctx =>
        {
            var feature = ctx.Features.Get<IExceptionHandlerFeature>();
            if (feature is not null)
            {
                ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Unhandled")
                    .LogError(feature.Error, "Unhandled exception on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            }
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(ApiError.ForStatus(500, ctx.Request.Path));
        }));

        app.UseStatusCodePages(async ctx =>
        {
            var response = ctx.HttpContext.Response;
            await response.WriteAsJsonAsync(ApiError.ForStatus(response.StatusCode, ctx.HttpContext.Request.Path));
        });
    }

    private static string Camel(string s) =>
        s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
