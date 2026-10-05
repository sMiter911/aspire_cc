package com.example.auth.config;

import io.swagger.v3.oas.models.Components;
import io.swagger.v3.oas.models.OpenAPI;
import io.swagger.v3.oas.models.info.Info;
import io.swagger.v3.oas.models.security.SecurityScheme;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

@Configuration
public class OpenApiConfig {

    public static final String BEARER = "bearerAuth";

    @Bean
    OpenAPI openApi() {
        return new OpenAPI()
                .info(new Info()
                        .title("Auth API")
                        .version("v1")
                        .description("""
                                Authentication & authorization API.

                                * Access token: short-lived JWT, returned in the response body, sent as `Authorization: Bearer`.
                                * Refresh token: opaque, rotated on every use, stored only as a hash, delivered as an \
                                HttpOnly cookie scoped to `/api/auth`.
                                * `POST /api/auth/refresh` and `/logout` additionally require the header \
                                `X-Requested-With: XMLHttpRequest` (CSRF defence).
                                """))
                .components(new Components().addSecuritySchemes(BEARER, new SecurityScheme()
                        .type(SecurityScheme.Type.HTTP)
                        .scheme("bearer")
                        .bearerFormat("JWT")));
    }
}
