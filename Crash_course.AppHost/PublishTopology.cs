using Aspire.Hosting.ApplicationModel;

/// <summary>
/// The topology that `aspire publish` turns into docker-compose.yaml (deployed by Coolify). Local `aspire run` uses the
/// executables/dev containers in Program.cs instead. Differences from local:
///   * backend, worker and frontend are container images (Dockerfile next to each service), not local processes
///   * the frontend is an nginx image: the single public origin, proxying /api to the two APIs
///   * no pgAdmin, no OpenTelemetry agent, no bind mounts; RabbitMQ topology is baked into its image
///   * secrets are plain parameters, supplied as environment variables (Coolify) instead of generated user secrets
/// </summary>
static class PublishTopology
{
    public static void Add(IDistributedApplicationBuilder builder)
    {
        // Dashboard: browser access needs the token, telemetry ingestion needs the API key (both are secrets).
        var dashboardToken = builder.AddParameter("dashboard-token", secret: true);
        var dashboardApiKey = builder.AddParameter("dashboard-api-key", secret: true);

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(d => d
                .WithEnvironment("DASHBOARD__FRONTEND__AUTHMODE", "BrowserToken")
                .WithEnvironment("DASHBOARD__FRONTEND__BROWSERTOKEN", dashboardToken)
                .WithEnvironment("DASHBOARD__OTLP__AUTHMODE", "ApiKey")
                .WithEnvironment("DASHBOARD__OTLP__PRIMARYAPIKEY", dashboardApiKey))
            .ConfigureComposeFile(file =>
            {
                // Coolify attaches every stack to its own network and proxy; a custom network only gets in the way.
                file.Networks.Clear();
                foreach (var service in file.Services.Values)
                {
                    service.Networks.Clear();
                    if (service.Ports.Count > 0) // publish through Coolify's proxy (domain -> exposed port), not a host port
                    {
                        service.Expose.AddRange(service.Ports.Select(p => p.Split(':')[^1]));
                        service.Ports.Clear();
                    }
                    service.Expose.RemoveAll(p => p.Contains("${")); // unresolved default-port placeholder
                    service.Restart = "unless-stopped"; // depends_on only waits for "started"; a service that raced its database retries
                }
            });

        // Aspire creates the databases at runtime only locally; in compose the image's init script does it.
        var postgres = builder.AddPostgres("postgres")
            .WithDataVolume("postgres-data")
            .WithDockerfile("../database/postgres");
        var authDb = postgres.AddDatabase("authdb");
        var todoDb = postgres.AddDatabase("tododb");

        var rabbitUser = builder.AddParameter("rabbitmq-user");
        var rabbitPassword = builder.AddParameter("rabbitmq-password", secret: true);
        var rabbit = builder.AddRabbitMQ("messaging", rabbitUser, rabbitPassword)
            .WithDataVolume("rabbitmq-data")
            .WithDockerfile("../messaging/rabbitmq");

#pragma warning disable ASPIRECERTIFICATES001 // experimental Aspire API; keeps Redis on plain TCP inside the compose network
        var redis = builder.AddRedis("redis")
            .WithoutHttpsCertificate()
            .WithDataVolume("redis-data")
            .WithPersistence(TimeSpan.FromSeconds(10), 1);
#pragma warning restore ASPIRECERTIFICATES001

        var jwtPrivateKey = builder.AddParameter("jwt-private-key", secret: true);
        var adminEmail = builder.AddParameter("admin-email");
        var adminPassword = builder.AddParameter("admin-password", secret: true);
        var publicUrl = builder.AddParameter("public-url"); // https://your.domain, the browser-facing origin

        var backend = builder.AddDockerfile("backend", "..", "backend/Dockerfile") // context = repo root (Flyway migrations live in database/)
            .WithHttpEndpoint(targetPort: 8080, env: "PORT")
            .WithEnvironment("SPRING_DATASOURCE_URL", ReferenceExpression.Create(
                $"jdbc:postgresql://{postgres.Resource.PrimaryEndpoint.Property(EndpointProperty.Host)}:{postgres.Resource.PrimaryEndpoint.Property(EndpointProperty.Port)}/{authDb.Resource.DatabaseName}"))
            .WithEnvironment("SPRING_DATASOURCE_USERNAME", postgres.Resource.UserNameReference)
            .WithEnvironment("SPRING_DATASOURCE_PASSWORD", postgres.Resource.PasswordParameter)
            .WithEnvironment("APP_JWT_PRIVATE_KEY", jwtPrivateKey)
            .WithEnvironment("APP_BOOTSTRAP_ADMIN_EMAIL", adminEmail)
            .WithEnvironment("APP_BOOTSTRAP_ADMIN_PASSWORD", adminPassword)
            .WithEnvironment("APP_CORS_ALLOWED_ORIGINS", publicUrl)
            .WithEnvironment("SERVER_FORWARD_HEADERS_STRATEGY", "native") // real client IP (rate limits, audit) from nginx/Traefik
            .WaitFor(authDb);

        var todoApi = builder.AddProject<Projects.TodoApi>("todo-api")
            .WithReference(todoDb)
            .WithReference(rabbit)
            .WaitFor(todoDb)
            .WaitFor(rabbit)
            .WaitFor(backend)
            .WithEnvironment("Auth__Issuer", "auth-api")
            .WithEnvironment("Auth__Audience", "todo-api")
            .WithEnvironment("Auth__MetadataAddress",
                ReferenceExpression.Create($"{backend.GetEndpoint("http")}/.well-known/openid-configuration"))
            .WithEnvironment("Auth__AllowInsecureMetadata", "true") // plain HTTP, but only on the private compose network
            .WithEnvironment("Database__MigrateOnStartup", "true")  // EF migrations applied at start (single instance)
            .WithEnvironment("Cors__AllowedOrigins__0", publicUrl)
            .WithEnvironment("OTEL_EXPORTER_OTLP_HEADERS", ReferenceExpression.Create($"x-otlp-api-key={dashboardApiKey}"))
            .WithEndpoint("http", e => e.TargetPort = 8080);

        builder.AddDockerfile("todo-worker", "../todo-worker")
            .WithHttpEndpoint(targetPort: 8081, env: "PORT")
            .WithEnvironment("AMQP_HOST", rabbit.Resource.PrimaryEndpoint.Property(EndpointProperty.Host))
            .WithEnvironment("AMQP_PORT", rabbit.Resource.PrimaryEndpoint.Property(EndpointProperty.Port))
            .WithEnvironment("AMQP_USER", rabbit.Resource.UserNameReference)
            .WithEnvironment("AMQP_PASSWORD", rabbit.Resource.PasswordParameter)
            .WithEnvironment("REDIS_ADDR", ReferenceExpression.Create(
                $"{redis.Resource.PrimaryEndpoint.Property(EndpointProperty.Host)}:{redis.Resource.PrimaryEndpoint.Property(EndpointProperty.Port)}"))
            .WithEnvironment("REDIS_PASSWORD", redis.Resource.PasswordParameter!)
            .WithEnvironment("WORKER_ID", "worker-01")
            .WaitFor(rabbit)
            .WaitFor(redis);

        builder.AddDockerfile("frontend", "../frontend")
            .WithHttpEndpoint(targetPort: 80)
            .WithEnvironment("BACKEND_URL", backend.GetEndpoint("http"))
            .WithEnvironment("TODO_API_URL", todoApi.GetEndpoint("http"))
            .WaitFor(backend)
            .WaitFor(todoApi);
    }
}
