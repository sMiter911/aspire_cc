package com.example.auth.security;

import com.example.auth.role.Role;
import com.example.auth.role.RoleRepository;
import com.example.auth.support.ApiClient;
import com.example.auth.support.IntegrationTest;
import com.example.auth.user.entity.User;
import com.example.auth.user.repository.UserRepository;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.security.oauth2.jose.jws.SignatureAlgorithm;
import org.springframework.security.oauth2.jwt.JwsHeader;
import org.springframework.security.oauth2.jwt.JwtClaimsSet;
import org.springframework.security.oauth2.jwt.JwtEncoderParameters;
import org.springframework.security.oauth2.jwt.NimbusJwtEncoder;
import org.springframework.test.web.servlet.MockMvc;
import com.nimbusds.jose.jwk.JWKSet;
import com.nimbusds.jose.jwk.RSAKey;
import com.nimbusds.jose.jwk.gen.RSAKeyGenerator;
import com.nimbusds.jose.jwk.source.ImmutableJWKSet;

import java.time.Duration;
import java.time.Instant;
import java.util.List;
import java.util.UUID;

import static com.example.auth.support.ApiClient.PASSWORD;
import static com.example.auth.support.ApiClient.accessToken;
import static com.example.auth.support.ApiClient.uniqueEmail;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.options;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.header;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

@IntegrationTest
class AuthorizationIT {

    @Autowired MockMvc mvc;
    @Autowired UserRepository users;
    @Autowired RoleRepository roles;
    @Autowired JwtService jwtService;

    ApiClient api;

    @BeforeEach
    void setUp() {
        api = new ApiClient(mvc);
    }

    private String bearerFor(boolean admin) throws Exception {
        String email = uniqueEmail();
        api.register(email, PASSWORD);
        if (admin) {
            User u = users.findByEmail(email).orElseThrow();
            u.getRoles().add(roles.findByName(Role.ADMIN).orElseThrow());
            users.save(u);
        }
        return "Bearer " + accessToken(api.login(email, PASSWORD));
    }

    @Test
    void unauthenticatedRequestIsRejectedWithJson401() throws Exception {
        mvc.perform(get("/api/users/me"))
                .andExpect(status().isUnauthorized())
                .andExpect(jsonPath("$.code").value("UNAUTHENTICATED"))
                .andExpect(jsonPath("$.path").value("/api/users/me"));
        mvc.perform(get("/api/admin/users")).andExpect(status().isUnauthorized());
    }

    @Test
    void authenticatedUserCanReadOwnProfileWithoutSecurityFields() throws Exception {
        mvc.perform(get("/api/users/me").header("Authorization", bearerFor(false)))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.roles[0]").value("USER"))
                .andExpect(jsonPath("$.passwordHash").doesNotExist())
                .andExpect(jsonPath("$.failedLoginAttempts").doesNotExist())
                .andExpect(jsonPath("$.accountLocked").doesNotExist());
    }

    @Test
    void userRoleCannotAccessAdminEndpoint() throws Exception {
        mvc.perform(get("/api/admin/users").header("Authorization", bearerFor(false)))
                .andExpect(status().isForbidden())
                .andExpect(jsonPath("$.code").value("ACCESS_DENIED"));
    }

    @Test
    void adminCanAccessAdminEndpoint() throws Exception {
        mvc.perform(get("/api/admin/users").header("Authorization", bearerFor(true)))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.content").isArray())
                .andExpect(jsonPath("$.content[0].passwordHash").doesNotExist())
                .andExpect(jsonPath("$.totalElements").isNumber());
    }

    @Test
    void adminPagingIsBounded() throws Exception {
        mvc.perform(get("/api/admin/users?size=100000").header("Authorization", bearerFor(true)))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.code").value("VALIDATION_ERROR"));
    }

    @Test
    void expiredAccessTokenIsRejected() throws Exception {
        String token = jwtService.sign(claims(Instant.now().minus(Duration.ofMinutes(20)),
                Instant.now().minusSeconds(5), "auth-api", jwtService.issuer()));
        mvc.perform(get("/api/users/me").header("Authorization", "Bearer " + token))
                .andExpect(status().isUnauthorized());
    }

    @Test
    void tokenSignedWithAnotherKeyIsRejected() throws Exception {
        RSAKey other = new RSAKeyGenerator(2048).keyID("attacker").generate();
        String token = forge(other, claims(Instant.now(), Instant.now().plusSeconds(300), "auth-api",
                jwtService.issuer()));
        mvc.perform(get("/api/users/me").header("Authorization", "Bearer " + token))
                .andExpect(status().isUnauthorized());
    }

    @Test
    void tokenForAnotherAudienceIsRejected() throws Exception {
        String token = jwtService.sign(claims(Instant.now(), Instant.now().plusSeconds(300), "some-other-api",
                jwtService.issuer()));
        mvc.perform(get("/api/users/me").header("Authorization", "Bearer " + token))
                .andExpect(status().isUnauthorized());
    }

    @Test
    void tokenFromAnotherIssuerIsRejected() throws Exception {
        String token = jwtService.sign(claims(Instant.now(), Instant.now().plusSeconds(300), "auth-api",
                "https://evil.example"));
        mvc.perform(get("/api/users/me").header("Authorization", "Bearer " + token))
                .andExpect(status().isUnauthorized());
    }

    @Test
    void garbageBearerTokenIsRejected() throws Exception {
        mvc.perform(get("/api/users/me").header("Authorization", "Bearer not.a.jwt"))
                .andExpect(status().isUnauthorized());
    }

    // ---- HTTP hardening -----------------------------------------------------------------------------

    @Test
    void healthIsPublicButOtherActuatorEndpointsAreClosed() throws Exception {
        mvc.perform(get("/actuator/health")).andExpect(status().isOk());
        mvc.perform(get("/actuator/health/readiness")).andExpect(status().isOk());
        mvc.perform(get("/actuator/env")).andExpect(status().isUnauthorized());
        mvc.perform(get("/actuator/heapdump")).andExpect(status().isUnauthorized());
    }

    @Test
    void docsAreNotExposedWhenDisabled() throws Exception {
        mvc.perform(get("/v3/api-docs")).andExpect(status().isUnauthorized());
        mvc.perform(get("/scalar")).andExpect(status().isUnauthorized());
    }

    @Test
    void securityHeadersArePresent() throws Exception {
        mvc.perform(get("/api/users/me"))
                .andExpect(header().string("X-Content-Type-Options", "nosniff"))
                .andExpect(header().string("X-Frame-Options", "DENY"))
                .andExpect(header().string("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'"))
                .andExpect(header().string("Referrer-Policy", "no-referrer"));
    }

    @Test
    void corsAllowsOnlyConfiguredOrigins() throws Exception {
        mvc.perform(options("/api/users/me").header("Origin", "http://localhost:5173")
                        .header("Access-Control-Request-Method", "GET"))
                .andExpect(status().isOk())
                .andExpect(header().string("Access-Control-Allow-Origin", "http://localhost:5173"))
                .andExpect(header().string("Access-Control-Allow-Credentials", "true"));
        mvc.perform(options("/api/users/me").header("Origin", "https://evil.example")
                        .header("Access-Control-Request-Method", "GET"))
                .andExpect(status().isForbidden());
    }

    private JwtClaimsSet claims(Instant issuedAt, Instant expiresAt, String audience, String issuer) {
        return JwtClaimsSet.builder()
                .issuer(issuer)
                .subject(UUID.randomUUID().toString())
                .audience(List.of(audience))
                .issuedAt(issuedAt)
                .expiresAt(expiresAt)
                .claim("roles", List.of("USER"))
                .build();
    }

    private String forge(RSAKey key, JwtClaimsSet claims) {
        var encoder = new NimbusJwtEncoder(new ImmutableJWKSet<>(new JWKSet(key)));
        return encoder.encode(JwtEncoderParameters.from(
                JwsHeader.with(SignatureAlgorithm.RS256).keyId(key.getKeyID()).build(), claims)).getTokenValue();
    }
}
