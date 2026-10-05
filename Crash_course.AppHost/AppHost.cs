using System.Security.Cryptography;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;

var builder = DistributedApplication.CreateBuilder(args);

// `aspire publish` (Docker Compose for Coolify) uses a separate, container-only topology. See PublishTopology.cs.
if (builder.ExecutionContext.IsPublishMode)
{
    PublishTopology.Add(builder);
    builder.Build().Run();
    return;
}

// =============================================================================================
// Infrastructure: ONE PostgreSQL server, TWO logically separate databases.
//   authdb  owned by the Spring Boot identity service (users, roles, refresh tokens)
//   tododb  owned by the ASP.NET Core Todo API (todos only)
// Neither service is given the other's connection details, so the service boundary is real.
// Aspire generates the server password into the AppHost user secrets; nothing is committed.
// =============================================================================================
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("auth-postgres-data")
    .WithPgAdmin(); // dev-only web UI for the databases, linked from the dashboard
var authDb = postgres.AddDatabase("authdb");
var todoDb = postgres.AddDatabase("tododb");

// RabbitMQ: the durable work queue between the Todo API and the Go worker. The topology (exchanges, queues,
// dead-letter queues, retry delay queues) lives in messaging/rabbitmq/definitions.json and is loaded by the broker
// itself at boot, so no service declares queues and there is one source of truth.
//
// RabbitMQ does not seed its default user when definitions are loaded, so the AppHost writes the broker's
// definitions file at startup: the repo's topology + the broker user with the Aspire-generated password (stored
// only as a salted hash in the generated file, under obj/, which is gitignored).
const string RabbitUser = "app";
var messagingDir = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "messaging", "rabbitmq"));
var rabbitUserParam = builder.AddParameter("rabbitmq-user", RabbitUser);
var rabbitPasswordParam = builder.AddParameter(
    "rabbitmq-password", new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true);
var rabbitDefinitions = WriteRabbitDefinitions(
    Path.Combine(messagingDir, "definitions.json"),
    Path.Combine(builder.AppHostDirectory, "obj", "rabbitmq", "definitions.json"),
    RabbitUser,
    await rabbitPasswordParam.Resource.GetValueAsync(CancellationToken.None) ?? throw new InvalidOperationException("rabbitmq-password"));

var rabbit = builder.AddRabbitMQ("messaging", rabbitUserParam, rabbitPasswordParam)
    .WithManagementPlugin()                     // dashboard link: queues, DLQs, message rates
    .WithDataVolume("auth-rabbitmq-data")
    .WithBindMount(rabbitDefinitions, "/etc/rabbitmq/definitions.json", isReadOnly: true)
    .WithBindMount(Path.Combine(messagingDir, "20-definitions.conf"), "/etc/rabbitmq/conf.d/20-definitions.conf", isReadOnly: true);

// Redis: transient job state for the worker (status, locks, release queue). NOT the work queue: if it is lost,
// no message is lost (RabbitMQ keeps them). Persistence is on so a restart does not forget pending releases.
#pragma warning disable ASPIRECERTIFICATES001 // experimental Aspire API; only used to keep local Redis on plain TCP
var redis = builder.AddRedis("redis")
    .WithoutHttpsCertificate()                  // dev: plain TCP on localhost (Aspire otherwise serves TLS on the primary port)
    .WithDataVolume("auth-redis-data")
    .WithPersistence(TimeSpan.FromSeconds(10), 1);
#pragma warning restore ASPIRECERTIFICATES001

// =============================================================================================
// Secrets / settings.
//   jwt-private-key  RSA key that signs access tokens (generated once, kept in user secrets). Only the Spring
//                    service gets it; the Todo API verifies with the public JWKS and holds no secret.
//   admin-email      plain setting (appsettings.Development.json)
//   admin-password   secret: dotnet user-secrets set Parameters:admin-password "<value>"
// =============================================================================================
var jwtPrivateKey = builder.AddParameter(
    "jwt-private-key", new RsaPrivateKeyDefault(), secret: true, persist: true);
var adminEmail = builder.AddParameter("admin-email");
var adminPassword = builder.AddParameter("admin-password", secret: true);

// =============================================================================================
// Spring Boot identity service (not a .NET project): the Maven wrapper runs `spring-boot:run`; Aspire allocates
// the HTTP port (PORT) and wires configuration through environment variables.
// =============================================================================================
var jdbcUrl = ReferenceExpression.Create(
    $"jdbc:postgresql://{postgres.Resource.PrimaryEndpoint.Property(EndpointProperty.Host)}:{postgres.Resource.PrimaryEndpoint.Property(EndpointProperty.Port)}/{authDb.Resource.DatabaseName}");

var backendDir = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "backend"));
var mavenWrapper = Path.Combine(backendDir, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw");

// Telemetry: the OpenTelemetry Java agent reads Aspire's OTEL_* variables and exports traces, metrics and logs to the
// dashboard. Downloaded once (dev only, gitignored) and attached to the application JVM only (not Maven itself).
var otelAgent = await EnsureOtelAgentAsync(Path.Combine(backendDir, "otel", "opentelemetry-javaagent.jar"));

var backend = builder.AddExecutable("backend", mavenWrapper, backendDir,
        "-B", "-ntp", "spring-boot:run", $"-Dspring-boot.run.jvmArguments=-javaagent:{otelAgent}")
    .WithHttpEndpoint(env: "PORT")
    // HTTP/protobuf to the dashboard's plain-HTTP OTLP endpoint (see launchSettings.json): the JVM does not trust
    // the ASP.NET dev certificate that the gRPC/HTTPS endpoint uses.
    .WithOtlpExporter(OtlpProtocol.HttpProtobuf)
    .WithEnvironment("SPRING_PROFILES_ACTIVE", "dev")
    .WithEnvironment("SPRING_DATASOURCE_URL", jdbcUrl)
    .WithEnvironment("SPRING_DATASOURCE_USERNAME", postgres.Resource.UserNameReference)
    .WithEnvironment("SPRING_DATASOURCE_PASSWORD", postgres.Resource.PasswordParameter)
    .WithEnvironment("APP_JWT_PRIVATE_KEY", jwtPrivateKey)
    .WithEnvironment("APP_BOOTSTRAP_ADMIN_EMAIL", adminEmail)
    .WithEnvironment("APP_BOOTSTRAP_ADMIN_PASSWORD", adminPassword)
    .WaitFor(authDb)
    .WithHttpHealthCheck("/actuator/health/readiness")
    .WithUrls(context =>
    {
        var http = context.GetEndpoint("http");
        context.Urls.Add(new ResourceUrlAnnotation { Url = $"{http.Url}/scalar", DisplayText = "Scalar API reference" });
        context.Urls.Add(new ResourceUrlAnnotation { Url = $"{http.Url}/actuator/health", DisplayText = "Health" });
    });

// Optional: pin the JDK that Maven uses (otherwise whatever JAVA_HOME / PATH resolves to).
//   dotnet user-secrets set Java:Home "C:\path\to\jdk-25"
var javaHome = builder.Configuration["Java:Home"];
if (!string.IsNullOrWhiteSpace(javaHome))
{
    backend.WithEnvironment("JAVA_HOME", javaHome);
}

// =============================================================================================
// ASP.NET Core Todo API: a pure resource API. It validates tokens issued by the Spring service (signature via the
// published JWKS, issuer, audience, lifetime) and owns only todo data. It never calls Spring per request.
// =============================================================================================
var todoApi = builder.AddProject<Projects.TodoApi>("todo-api")
    .WithReference(todoDb)                       // ConnectionStrings__tododb
    .WithReference(rabbit)                       // ConnectionStrings__messaging (outbox publisher + status consumer)
    .WaitFor(todoDb)
    .WaitFor(rabbit)
    .WaitFor(backend)                            // the JWKS must be fetchable
    .WithEnvironment("Auth__Issuer", "auth-api")
    .WithEnvironment("Auth__Audience", "todo-api")
    .WithEnvironment("Auth__MetadataAddress",
        ReferenceExpression.Create($"{backend.GetEndpoint("http")}/.well-known/openid-configuration"))
    .WithEnvironment("Auth__AllowInsecureMetadata", "true")   // plain HTTP between local processes only
    .WithEnvironment("Auth__MetadataRefreshSeconds", "30")    // pick up a rotated signing key quickly in dev
    .WithEnvironment("Database__MigrateOnStartup", "true")    // dev convenience; deployments run `dotnet ef`
    .WithHttpHealthCheck("/api/health")
    .WithUrls(context =>
    {
        var http = context.GetEndpoint("http");
        context.Urls.Add(new ResourceUrlAnnotation { Url = $"{http.Url}/scalar", DisplayText = "Scalar API reference" });
        context.Urls.Add(new ResourceUrlAnnotation { Url = $"{http.Url}/api/health", DisplayText = "Health" });
    });

// =============================================================================================
// Go todo worker: consumes TodoCreated from RabbitMQ, processes jobs concurrently (bounded), tracks state in Redis,
// reports status back through RabbitMQ and releases finished jobs on a timer or on an ADMIN command. It owns no
// HTTP API except health, and no database: the Todo API stays the owner of the Todo domain.
// Tunables come from AppHost configuration (appsettings.Development.json "Worker" section).
// =============================================================================================
var workerDir = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "todo-worker"));
string Worker(string key, string fallback) => builder.Configuration[$"Worker:{key}"] ?? fallback;

var worker = builder.AddExecutable("todo-worker", "go", workerDir, "run", "./cmd/worker")
    .WithHttpEndpoint(env: "PORT")                       // /health and /ready only
    .WithEnvironment("AMQP_HOST", rabbit.Resource.PrimaryEndpoint.Property(EndpointProperty.Host))
    .WithEnvironment("AMQP_PORT", rabbit.Resource.PrimaryEndpoint.Property(EndpointProperty.Port))
    .WithEnvironment("AMQP_USER", rabbit.Resource.UserNameReference)
    .WithEnvironment("AMQP_PASSWORD", rabbit.Resource.PasswordParameter)
    .WithEnvironment("REDIS_ADDR", ReferenceExpression.Create(
        $"{redis.Resource.PrimaryEndpoint.Property(EndpointProperty.Host)}:{redis.Resource.PrimaryEndpoint.Property(EndpointProperty.Port)}"))
    .WithEnvironment("REDIS_PASSWORD", redis.Resource.PasswordParameter!)
    .WithEnvironment("WORKER_ID", "worker-01")
    .WithEnvironment("WORKER_CONCURRENCY", Worker("Concurrency", "5"))
    .WithEnvironment("RELEASE_INTERVAL", Worker("ReleaseInterval", "5m"))
    .WithEnvironment("MAX_RETRIES", Worker("MaxRetries", "3"))
    .WithEnvironment("PROCESS_DURATION", Worker("ProcessDuration", "3s"))
    .WithEnvironment("PROCESS_FAIL_RATE", Worker("FailRate", "0"))
    .WithReferenceRelationship(rabbit)
    .WithReferenceRelationship(redis)
    .WaitFor(rabbit)
    .WaitFor(redis)
    .WithHttpHealthCheck("/ready")
    .WithUrls(context =>
    {
        var http = context.GetEndpoint("http");
        context.Urls.Add(new ResourceUrlAnnotation { Url = $"{http.Url}/ready", DisplayText = "Ready" });
    });

// =============================================================================================
// React (Vite). The browser only talks to the Vite origin. Vite proxies /api/todos and /api/admin/todos to the
// Todo API and everything else under /api to Spring Boot, so cookies stay first-party and no CORS is needed in dev.
// =============================================================================================
var frontend = builder.AddViteApp("frontend", "../frontend")
    .WithEnvironment("BACKEND_URL", backend.GetEndpoint("http"))
    .WithEnvironment("TODO_API_URL", todoApi.GetEndpoint("http"))
    .WaitFor(backend)
    .WaitFor(todoApi)
    .WithExternalHttpEndpoints();

// Explicit origin allow-lists (used for Origin checks on cookie endpoints / non-proxied browser access).
backend.WithEnvironment("APP_CORS_ALLOWED_ORIGINS", frontend.GetEndpoint("http"));
todoApi.WithEnvironment("Cors__AllowedOrigins__0", frontend.GetEndpoint("http"));

builder.Build().Run();

/// <summary>Merges the committed topology with the broker user (RabbitMQ's salted SHA-256 password hash).</summary>
static string WriteRabbitDefinitions(string topologyPath, string outputPath, string user, string password)
{
    var definitions = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(topologyPath))!.AsObject();

    var salt = RandomNumberGenerator.GetBytes(4);
    var hash = SHA256.HashData([.. salt, .. System.Text.Encoding.UTF8.GetBytes(password)]);
    definitions["users"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
    {
        ["name"] = user,
        ["password_hash"] = Convert.ToBase64String([.. salt, .. hash]),
        ["hashing_algorithm"] = "rabbit_password_hashing_sha256",
        ["tags"] = "administrator",
    });
    definitions["permissions"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
    {
        ["user"] = user, ["vhost"] = "/", ["configure"] = ".*", ["write"] = ".*", ["read"] = ".*",
    });

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, definitions.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return outputPath;
}

static async Task<string> EnsureOtelAgentAsync(string path)
{
    if (!File.Exists(path))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var http = new HttpClient();
        await using var stream = await http.GetStreamAsync(
            "https://github.com/open-telemetry/opentelemetry-java-instrumentation/releases/latest/download/opentelemetry-javaagent.jar");
        await using var file = File.Create(path);
        await stream.CopyToAsync(file);
    }
    return path;
}

/// <summary>
/// Generates a 2048-bit RSA private key as Base64 PKCS#8 (single line, env-var friendly). With persist: true Aspire
/// stores it in the AppHost user secrets, so the key survives restarts and never enters source control.
/// </summary>
sealed class RsaPrivateKeyDefault : ParameterDefault
{
    public override string GetDefaultValue()
    {
        using var rsa = RSA.Create(2048);
        return Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
    }

    public override void WriteToManifest(ManifestPublishingContext context) =>
        throw new NotSupportedException("The signing key is generated locally and must be supplied when publishing.");
}
