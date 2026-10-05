package com.example.auth.security;

import com.example.auth.config.AppProperties;
import com.example.auth.role.Permission;
import com.example.auth.role.Role;
import com.example.auth.user.entity.User;
import com.nimbusds.jose.JOSEException;
import com.nimbusds.jose.jwk.JWKSet;
import com.nimbusds.jose.jwk.RSAKey;
import com.nimbusds.jose.jwk.source.ImmutableJWKSet;
import lombok.extern.slf4j.Slf4j;
import org.springframework.security.oauth2.jose.jws.SignatureAlgorithm;
import org.springframework.security.oauth2.jwt.JwsHeader;
import org.springframework.security.oauth2.jwt.JwtClaimsSet;
import org.springframework.security.oauth2.jwt.JwtEncoderParameters;
import org.springframework.security.oauth2.jwt.NimbusJwtEncoder;
import org.springframework.stereotype.Service;

import java.security.KeyFactory;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.interfaces.RSAPrivateCrtKey;
import java.security.interfaces.RSAPublicKey;
import java.security.spec.PKCS8EncodedKeySpec;
import java.security.spec.RSAPublicKeySpec;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Base64;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;
import java.util.UUID;

/**
 * Issues short-lived RS256 access tokens and publishes the public verification key.
 * <p>
 * Token contract (consumed by this API and by the ASP.NET Core Todo API):
 * iss, sub (immutable user id), aud (this API plus resource APIs), email, roles[], permissions[], iat, exp, jti.
 */
@Slf4j
@Service
public class JwtService {

    private final RSAKey jwk;
    private final NimbusJwtEncoder encoder;
    private final AppProperties.Jwt config;
    private final Clock clock;

    public JwtService(AppProperties props, Clock clock) {
        this.config = props.jwt();
        this.clock = clock;
        this.jwk = loadOrGenerateKey(config);
        this.encoder = new NimbusJwtEncoder(new ImmutableJWKSet<>(new JWKSet(jwk)));
    }

    public RSAPublicKey publicKey() {
        try {
            return jwk.toRSAPublicKey();
        } catch (JOSEException e) {
            throw new IllegalStateException(e);
        }
    }

    /** Public JWK Set (never contains private parameters). */
    public Map<String, Object> publicJwks() {
        return new JWKSet(jwk).toPublicJWKSet().toJSONObject();
    }

    public String issuer() {
        return config.issuer();
    }

    public String audience() {
        return config.audience();
    }

    public Duration accessTtl() {
        return config.accessTtl();
    }

    public String createAccessToken(User user) {
        Instant now = clock.instant();
        Set<String> roles = new TreeSet<>();
        Set<String> permissions = new TreeSet<>();
        for (Role role : user.getRoles()) {
            roles.add(role.getName());
            role.getPermissions().stream().map(Permission::getName).forEach(permissions::add);
        }
        List<String> audiences = new ArrayList<>();
        audiences.add(config.audience());
        audiences.addAll(config.additionalAudiences());
        return sign(JwtClaimsSet.builder()
                .issuer(config.issuer())
                .subject(user.getId().toString())
                .audience(audiences)
                .id(UUID.randomUUID().toString())
                .issuedAt(now)
                .expiresAt(now.plus(config.accessTtl()))
                .claim("email", user.getEmail())
                .claim("roles", List.copyOf(roles))
                .claim("permissions", List.copyOf(permissions))
                .build());
    }

    /** Signs arbitrary claims with the service key (tests use it to craft expired or unusual tokens). */
    public String sign(JwtClaimsSet claims) {
        JwsHeader header = JwsHeader.with(SignatureAlgorithm.RS256).keyId(jwk.getKeyID()).build();
        return encoder.encode(JwtEncoderParameters.from(header, claims)).getTokenValue();
    }

    private static RSAKey loadOrGenerateKey(AppProperties.Jwt config) {
        try {
            KeyPair pair;
            String encoded = config.privateKey();
            if (encoded != null && !encoded.isBlank()) {
                KeyFactory kf = KeyFactory.getInstance("RSA");
                RSAPrivateCrtKey priv = (RSAPrivateCrtKey) kf.generatePrivate(
                        new PKCS8EncodedKeySpec(Base64.getDecoder().decode(encoded.trim())));
                RSAPublicKey pub = (RSAPublicKey) kf.generatePublic(
                        new RSAPublicKeySpec(priv.getModulus(), priv.getPublicExponent()));
                pair = new KeyPair(pub, priv);
            } else if (config.allowEphemeralKey()) {
                log.warn("No app.jwt.private-key configured: generated a throwaway signing key. "
                        + "Issued tokens stop validating when this process restarts.");
                KeyPairGenerator gen = KeyPairGenerator.getInstance("RSA");
                gen.initialize(2048);
                pair = gen.generateKeyPair();
            } else {
                throw new IllegalStateException("app.jwt.private-key (APP_JWT_PRIVATE_KEY) is required");
            }
            return new RSAKey.Builder((RSAPublicKey) pair.getPublic())
                    .privateKey(pair.getPrivate())
                    .keyIDFromThumbprint()
                    .build();
        } catch (IllegalStateException e) {
            throw e;
        } catch (Exception e) {
            throw new IllegalStateException("Invalid app.jwt.private-key (expected Base64 PKCS#8 RSA key)", e);
        }
    }
}
