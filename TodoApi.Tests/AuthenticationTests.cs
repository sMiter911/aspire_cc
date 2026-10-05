using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace TodoApi.Tests;

/// <summary>Unauthenticated access and token attacks. Every case must end in 401 (or 403 where the token is valid
/// but carries no usable role) and never reach the data.</summary>
[Collection("api")]
public class AuthenticationTests(TodoApiFactory factory)
{
    private static string Url => "/api/todos";

    private async Task<HttpStatusCode> GetWith(string? token) =>
        (await factory.ClientFor(token).GetAsync(Url)).StatusCode;

    // ---- unauthenticated ---------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/api/todos")]
    [InlineData("POST", "/api/todos")]
    [InlineData("GET", "/api/todos/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT", "/api/todos/00000000-0000-0000-0000-000000000001")]
    [InlineData("PATCH", "/api/todos/00000000-0000-0000-0000-000000000001/complete")]
    [InlineData("DELETE", "/api/todos/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/admin/todos")]
    public async Task Anonymous_requests_are_rejected_with_401(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT") request.Content = Json.Body("""{"title":"x","isCompleted":false}""");

        var response = await factory.ClientFor().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UNAUTHENTICATED", body.GetProperty("code").GetString());
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Unknown_routes_are_not_served_to_anonymous_callers()
    {
        // Deny-by-default fallback policy.
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientFor().GetAsync("/api/nothing-here")).StatusCode);
    }

    // ---- token attacks -----------------------------------------------------------------------------

    [Fact]
    public async Task Valid_token_is_accepted()
    {
        Assert.Equal(HttpStatusCode.OK, await GetWith(Tokens.Create()));
    }

    [Fact]
    public async Task Expired_token_is_rejected() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await GetWith(Tokens.Create(lifetime: TimeSpan.FromMinutes(-1))));

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    [InlineData("")]
    public async Task Malformed_token_is_rejected(string token) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await GetWith(token));

    [Fact]
    public async Task Token_signed_with_an_unknown_key_is_rejected() =>
        Assert.Equal(HttpStatusCode.Unauthorized,
            await GetWith(Tokens.Create(roles: ["ADMIN"], key: TodoApiFactory.NewKey("attacker"))));

    [Fact]
    public async Task Token_with_the_right_kid_but_wrong_key_material_is_rejected() =>
        Assert.Equal(HttpStatusCode.Unauthorized,
            await GetWith(Tokens.Create(key: TodoApiFactory.NewKey(TodoApiFactory.SigningKey.KeyId))));

    [Fact]
    public async Task Wrong_issuer_is_rejected() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await GetWith(Tokens.Create(issuer: "https://evil.example")));

    [Fact]
    public async Task Wrong_audience_is_rejected() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await GetWith(Tokens.Create(audience: "some-other-api")));

    [Fact]
    public async Task Missing_subject_is_rejected() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await GetWith(Tokens.Create(includeSub: false)));

    [Fact]
    public async Task Non_guid_subject_is_rejected()
    {
        // sub must be the Spring user id; an email or free text must not become an owner key.
        var claims = new Dictionary<string, object> { ["sub"] = "admin@example.com", ["roles"] = new[] { "ADMIN" } };
        var token = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TodoApiFactory.Issuer,
            Audience = TodoApiFactory.Audience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(TodoApiFactory.SigningKey, SecurityAlgorithms.RsaSha256),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, await GetWith(token));
    }

    [Fact]
    public async Task Token_without_a_role_is_authenticated_but_forbidden() =>
        Assert.Equal(HttpStatusCode.Forbidden, await GetWith(Tokens.Create(includeRoles: false)));

    [Fact]
    public async Task Token_with_an_unknown_role_is_forbidden() =>
        Assert.Equal(HttpStatusCode.Forbidden, await GetWith(Tokens.Create(roles: ["GUEST"])));

    [Fact]
    public async Task Tampered_role_claim_invalidates_the_signature()
    {
        var valid = Tokens.Create(roles: ["USER"]);
        var parts = valid.Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1]));
        var tampered = payload.Replace("\"USER\"", "\"ADMIN\"");
        Assert.NotEqual(payload, tampered);
        var forged = $"{parts[0]}.{Base64UrlEncoder.Encode(tampered)}.{parts[2]}";

        var response = await factory.ClientFor(forged).GetAsync("/api/admin/todos");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unsigned_alg_none_token_is_rejected()
    {
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode(
            $$"""{"sub":"{{Guid.NewGuid()}}","roles":["ADMIN"],"iss":"auth-api","aud":"todo-api","exp":{{DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()}}}""");

        Assert.Equal(HttpStatusCode.Unauthorized, await GetWith($"{header}.{payload}."));
    }

    [Fact]
    public async Task Hs256_token_signed_with_the_public_key_is_rejected()
    {
        // Classic algorithm-confusion attack: use the (public) RSA key bytes as an HMAC secret.
        var publicBytes = SigningCredentialsPublicBytes();
        var token = Tokens.Create(roles: ["ADMIN"], key: new SymmetricSecurityKey(publicBytes), algorithm: SecurityAlgorithms.HmacSha256);
        Assert.Equal(HttpStatusCode.Unauthorized, await GetWith(token));
    }

    private static byte[] SigningCredentialsPublicBytes()
    {
        var p = TodoApiFactory.SigningKey.Rsa.ExportParameters(false);
        return SHA256.HashData(p.Modulus!);
    }
}
