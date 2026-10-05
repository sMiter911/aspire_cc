package com.example.auth.security;

import io.swagger.v3.oas.annotations.Hidden;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RestController;
import org.springframework.web.servlet.support.ServletUriComponentsBuilder;

import java.util.List;
import java.util.Map;

/**
 * Lets resource APIs (the ASP.NET Core Todo API) discover the issuer and fetch the public signing keys.
 * Only public key material is served.
 */
@Hidden
@RestController
public class WellKnownController {

    private final JwtService jwt;

    public WellKnownController(JwtService jwt) {
        this.jwt = jwt;
    }

    @GetMapping("/.well-known/jwks.json")
    public Map<String, Object> jwks() {
        return jwt.publicJwks();
    }

    @GetMapping("/.well-known/openid-configuration")
    public Map<String, Object> discovery() {
        String base = ServletUriComponentsBuilder.fromCurrentContextPath().build().toUriString();
        return Map.of(
                "issuer", jwt.issuer(),
                "jwks_uri", base + "/.well-known/jwks.json",
                "id_token_signing_alg_values_supported", List.of("RS256"));
    }
}
