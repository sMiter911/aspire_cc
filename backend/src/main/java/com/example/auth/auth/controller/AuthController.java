package com.example.auth.auth.controller;

import com.example.auth.auth.dto.AuthResponse;
import com.example.auth.auth.dto.LoginRequest;
import com.example.auth.auth.dto.RegisterRequest;
import com.example.auth.auth.service.AuthService;
import com.example.auth.auth.service.RefreshCookieFactory;
import com.example.auth.common.ClientInfo;
import com.example.auth.config.OpenApiConfig;
import com.example.auth.exception.ApiError;
import com.example.auth.user.dto.UserResponse;
import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.media.Content;
import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.responses.ApiResponse;
import io.swagger.v3.oas.annotations.security.SecurityRequirement;
import io.swagger.v3.oas.annotations.tags.Tag;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.validation.Valid;
import org.springframework.http.HttpHeaders;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.security.oauth2.jwt.Jwt;
import org.springframework.web.bind.annotation.CookieValue;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

import java.util.UUID;

@RestController
@RequestMapping("/api/auth")
@Tag(name = "Authentication")
@ApiResponse(responseCode = "429", description = "Rate limited (see Retry-After)",
        content = @Content(schema = @Schema(implementation = ApiError.class)))
public class AuthController {

    private final AuthService auth;
    private final RefreshCookieFactory cookies;

    public AuthController(AuthService auth, RefreshCookieFactory cookies) {
        this.auth = auth;
        this.cookies = cookies;
    }

    @PostMapping("/register")
    @Operation(summary = "Register a new account",
            description = "Creates a USER account. Does not sign the caller in.")
    @ApiResponse(responseCode = "201", description = "Account created")
    @ApiResponse(responseCode = "400", description = "Validation failed",
            content = @Content(schema = @Schema(implementation = ApiError.class)))
    @ApiResponse(responseCode = "409", description = "Email already registered",
            content = @Content(schema = @Schema(implementation = ApiError.class)))
    public ResponseEntity<UserResponse> register(@Valid @RequestBody RegisterRequest request,
            HttpServletRequest http) {
        return ResponseEntity.status(HttpStatus.CREATED).body(auth.register(request, ClientInfo.from(http)));
    }

    @PostMapping("/login")
    @Operation(summary = "Sign in",
            description = "Returns a short-lived access token in the body and sets the HttpOnly refresh cookie.")
    @ApiResponse(responseCode = "200", description = "Signed in")
    @ApiResponse(responseCode = "401", description = "Invalid credentials (identical for every failure cause)",
            content = @Content(schema = @Schema(implementation = ApiError.class)))
    public ResponseEntity<AuthResponse> login(@Valid @RequestBody LoginRequest request, HttpServletRequest http) {
        AuthService.Session session = auth.login(request, ClientInfo.from(http));
        return withRefreshCookie(session);
    }

    @PostMapping("/refresh")
    @Operation(summary = "Rotate the refresh token and get a new access token",
            description = "Authenticated by the refresh cookie. Requires header `X-Requested-With: XMLHttpRequest`. "
                    + "The presented token is single-use; replaying it revokes the whole session.")
    @ApiResponse(responseCode = "200", description = "Rotated")
    @ApiResponse(responseCode = "401", description = "Refresh token missing, expired, revoked or reused",
            content = @Content(schema = @Schema(implementation = ApiError.class)))
    @ApiResponse(responseCode = "403", description = "CSRF header or origin check failed",
            content = @Content(schema = @Schema(implementation = ApiError.class)))
    public ResponseEntity<?> refresh(
            @CookieValue(name = "${app.refresh.cookie-name:refresh_token}", required = false) String refreshToken,
            HttpServletRequest http) {
        try {
            return withRefreshCookie(auth.refresh(refreshToken, ClientInfo.from(http)));
        } catch (com.example.auth.exception.ApiException e) {
            // Drop the dead cookie so the browser stops sending it.
            if (e.getStatus() == HttpStatus.UNAUTHORIZED) {
                return ResponseEntity.status(e.getStatus())
                        .header(HttpHeaders.SET_COOKIE, cookies.clear().toString())
                        .body(ApiError.of(e.getStatus().value(), e.getCode(), e.getMessage(),
                                http.getRequestURI(), java.util.List.of()));
            }
            throw e;
        }
    }

    @PostMapping("/logout")
    @Operation(summary = "Sign out this session",
            description = "Revokes the session behind the refresh cookie and clears it. Idempotent. "
                    + "Requires header `X-Requested-With: XMLHttpRequest`.")
    @ApiResponse(responseCode = "204", description = "Signed out")
    public ResponseEntity<Void> logout(
            @CookieValue(name = "${app.refresh.cookie-name:refresh_token}", required = false) String refreshToken) {
        auth.logout(refreshToken);
        return ResponseEntity.noContent().header(HttpHeaders.SET_COOKIE, cookies.clear().toString()).build();
    }

    @PostMapping("/logout-all")
    @SecurityRequirement(name = OpenApiConfig.BEARER)
    @Operation(summary = "Sign out everywhere",
            description = "Revokes every refresh token of the authenticated user. Existing access tokens remain "
                    + "valid until they expire (minutes).")
    @ApiResponse(responseCode = "204", description = "All sessions revoked")
    @ApiResponse(responseCode = "401", description = "Not authenticated",
            content = @Content(schema = @Schema(implementation = ApiError.class)))
    public ResponseEntity<Void> logoutAll(@AuthenticationPrincipal Jwt jwt) {
        auth.logoutAll(UUID.fromString(jwt.getSubject()));
        return ResponseEntity.noContent().header(HttpHeaders.SET_COOKIE, cookies.clear().toString()).build();
    }

    private ResponseEntity<AuthResponse> withRefreshCookie(AuthService.Session session) {
        return ResponseEntity.ok()
                .header(HttpHeaders.SET_COOKIE, cookies.create(session.refreshToken()).toString())
                .header(HttpHeaders.CACHE_CONTROL, "no-store")
                .body(session.response());
    }
}
