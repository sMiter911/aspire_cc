using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace TodoApi.Tests;

/// <summary>
/// The real application (real EF migrations, real PostgreSQL via Testcontainers, real JWT validation pipeline).
/// The only substitution: the signing key comes from a test key pair instead of the Spring service's JWKS endpoint.
/// </summary>
public sealed class TodoApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public const string Issuer = "auth-api";
    public const string Audience = "todo-api";

    public static readonly RsaSecurityKey SigningKey = NewKey("test-key");

    public static RsaSecurityKey NewKey(string kid) => new(RSA.Create(2048)) { KeyId = kid };

    public async Task InitializeAsync() => await _db.StartAsync();

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _db.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("ConnectionStrings:tododb", _db.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Auth:Issuer", Issuer);
        builder.UseSetting("Auth:Audience", Audience);

        builder.ConfigureTestServices(services =>
        {
            var config = new OpenIdConnectConfiguration { Issuer = Issuer };
            config.SigningKeys.Add(SigningKey);
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
            });
        });
    }

    public HttpClient ClientFor(string? token = null)
    {
        var client = CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<TodoApiFactory>;

/// <summary>Mints access tokens shaped like the ones the Spring Boot service issues.</summary>
public static class Tokens
{
    public static string Create(
        Guid? sub = null,
        string[]? roles = null,
        string? email = null,
        string issuer = TodoApiFactory.Issuer,
        string audience = TodoApiFactory.Audience,
        TimeSpan? lifetime = null,
        SecurityKey? key = null,
        string algorithm = SecurityAlgorithms.RsaSha256,
        bool includeSub = true,
        bool includeRoles = true)
    {
        var claims = new Dictionary<string, object>();
        if (includeSub) claims["sub"] = (sub ?? Guid.NewGuid()).ToString();
        claims["email"] = email ?? "user@example.com";
        if (includeRoles) claims["roles"] = roles ?? ["USER"];

        var now = DateTime.UtcNow;
        var life = lifetime ?? TimeSpan.FromMinutes(10);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = life < TimeSpan.Zero ? now + life - TimeSpan.FromMinutes(5) : now,
            NotBefore = life < TimeSpan.Zero ? now + life - TimeSpan.FromMinutes(5) : now,
            Expires = now + life,
            SigningCredentials = new SigningCredentials(key ?? TodoApiFactory.SigningKey, algorithm),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static string User(Guid id) => Create(id, ["USER"]);
    public static string Admin(Guid id) => Create(id, ["USER", "ADMIN"]);
}

public static class Json
{
    public static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    public static async Task<Guid> CreateTodoAsync(HttpClient client, string title = "Buy milk")
    {
        var response = await client.PostAsJsonAsync("/api/todos", new { title });
        response.EnsureSuccessStatusCode();
        var doc = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return doc.GetProperty("id").GetGuid();
    }
}
