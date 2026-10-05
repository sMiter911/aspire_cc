using Microsoft.AspNetCore.OpenApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using TodoApi.Authentication;
using TodoApi.Authorization;
using TodoApi.Data;
using TodoApi.Messaging;
using TodoApi.Middleware;
using TodoApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultHealthChecks();

// --- Database: Aspire injects ConnectionStrings__tododb. Health check + tracing come with the integration. ---
builder.AddNpgsqlDbContext<TodoDbContext>("tododb");

// --- Security: authentication (tokens from the Spring identity service) and authorization (policies) -------
builder.Services.AddIdentityAuthentication(builder.Configuration);
builder.Services.AddTodoAuthorization();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<TodoService>();
builder.Services.AddScoped<TodoProcessingService>();

// --- Messaging: transactional outbox -> RabbitMQ (todo.created / todo.release), status reports <- the Go worker ---
builder.Services.Configure<MessagingOptions>(builder.Configuration.GetSection(MessagingOptions.Section));
builder.Services.AddHealthChecks().AddCheck<OutboxHealthCheck>("outbox");
if (builder.Configuration.GetValue("Messaging:Enabled", true))
{
    // Health: the broker being down must not make the API "unhealthy"; the outbox check reports Degraded instead.
    builder.AddRabbitMQClient("messaging", settings => settings.DisableHealthChecks = true);
    builder.Services.AddSingleton<BrokerConnection>();
    builder.Services.AddHostedService<OutboxPublisher>();
    builder.Services.AddHostedService<StatusConsumer>();
}

builder.Services.AddControllers();
builder.Services.AddApiErrorHandling();

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64 * 1024);

// --- CORS: explicit origins only. Empty (default) means no cross-origin access: the SPA is same-origin via proxy.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
        .WithOrigins(corsOrigins)
        .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
        .WithHeaders("Authorization", "Content-Type")));
}

// --- OpenAPI (+ Scalar in development) ---------------------------------------------------------------
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info = new OpenApiInfo
    {
        Title = "Todo API",
        Version = "v1",
        Description = "Resource API. Authenticate with an access token issued by the Spring Boot identity service "
                      + "(`Authorization: Bearer ...`). Users see their own todos; ADMIN can access all. "
                      + "A todo you may not access returns 404, the same as one that does not exist.",
    };
    doc.Components ??= new OpenApiComponents();
    doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
    doc.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
    };
    doc.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", doc)] = [] }];
    return Task.CompletedTask;
}));

var app = builder.Build();

// Controlled schema management: EF migrations only. Auto-apply is opt-in (Aspire enables it for local dev);
// otherwise run `dotnet ef database update` or a migrations bundle as a deployment step.
if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<TodoDbContext>().Database.MigrateAsync();
}

app.UseApiErrorHandling();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers.XFrameOptions = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.Headers.CacheControl = "no-store";
    }
    await next();
});

if (corsOrigins.Length > 0)
{
    app.UseCors();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapDefaultEndpoints(); // /api/health, /api/health/live

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference(o => o.WithTitle("Todo API")).AllowAnonymous();
}

app.Run();

public partial class Program;
