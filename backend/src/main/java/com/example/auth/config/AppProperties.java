package com.example.auth.config;

import jakarta.validation.Valid;
import jakarta.validation.constraints.Min;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.NotEmpty;
import jakarta.validation.constraints.NotNull;
import org.springframework.boot.context.properties.ConfigurationProperties;
import org.springframework.boot.context.properties.bind.DefaultValue;
import org.springframework.validation.annotation.Validated;

import java.time.Duration;
import java.util.List;

/**
 * All custom settings live under the {@code app.*} prefix. Secrets (JWT key, bootstrap admin password)
 * have no defaults and must come from the environment / Aspire parameters.
 */
@Validated
@ConfigurationProperties(prefix = "app")
public record AppProperties(
        @Valid @NotNull Jwt jwt,
        @Valid @NotNull Refresh refresh,
        @Valid @NotNull Cors cors,
        @Valid @NotNull Lockout lockout,
        @Valid @NotNull RateLimit rateLimit,
        @Valid BootstrapAdmin bootstrapAdmin) {

    public record Jwt(
            /*
             * RSA private key, PKCS#8 DER, Base64 (single line). Signs access tokens (RS256); only the public half is
             * published (JWKS), so resource APIs can verify tokens without holding any secret. Never commit.
             */
            String privateKey,
            /* Dev/test convenience: generate a throwaway key at startup when no private key is configured. */
            @DefaultValue("false") boolean allowEphemeralKey,
            @NotBlank @DefaultValue("auth-api") String issuer,
            /* This API's own audience; tokens presented to it must contain it. */
            @NotBlank @DefaultValue("auth-api") String audience,
            /* Other resource APIs the issued tokens are intended for. */
            @NotNull @DefaultValue("todo-api") List<String> additionalAudiences,
            @NotNull @DefaultValue("10m") Duration accessTtl) {
    }

    public record Refresh(
            @NotNull @DefaultValue("14d") Duration ttl,
            @NotBlank @DefaultValue("refresh_token") String cookieName,
            @NotBlank @DefaultValue("/api/auth") String cookiePath,
            @DefaultValue("true") boolean cookieSecure,
            @NotBlank @DefaultValue("Strict") String cookieSameSite) {
    }

    public record Cors(@NotEmpty List<String> allowedOrigins) {
    }

    public record Lockout(
            @Min(1) @DefaultValue("5") int maxFailedAttempts,
            @NotNull @DefaultValue("15m") Duration duration) {
    }

    public record RateLimit(
            @DefaultValue("true") boolean enabled,
            @Valid @NotNull @DefaultValue Policy login,
            @Valid @NotNull @DefaultValue Policy register,
            @Valid @NotNull @DefaultValue Policy refresh) {

        /** {@code capacity} requests per {@code window} per key. */
        public record Policy(
                @Min(1) @DefaultValue("10") int capacity,
                @NotNull @DefaultValue("1m") Duration window) {
        }
    }

    public record BootstrapAdmin(String email, String password) {
    }
}
