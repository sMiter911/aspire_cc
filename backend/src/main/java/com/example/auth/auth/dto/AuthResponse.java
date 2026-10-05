package com.example.auth.auth.dto;

import com.example.auth.user.dto.UserResponse;
import io.swagger.v3.oas.annotations.media.Schema;

/** Body of login/refresh. The refresh token is NOT here: it travels only in an HttpOnly cookie. */
public record AuthResponse(
        @Schema(description = "Short-lived JWT; keep in memory, send as Authorization: Bearer") String accessToken,
        @Schema(example = "Bearer") String tokenType,
        @Schema(description = "Access token lifetime in seconds", example = "600") long expiresIn,
        UserResponse user) {

    public static AuthResponse bearer(String accessToken, long expiresInSeconds, UserResponse user) {
        return new AuthResponse(accessToken, "Bearer", expiresInSeconds, user);
    }
}
