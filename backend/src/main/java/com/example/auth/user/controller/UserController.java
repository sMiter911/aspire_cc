package com.example.auth.user.controller;

import com.example.auth.config.OpenApiConfig;
import com.example.auth.exception.ApiError;
import com.example.auth.user.dto.UserResponse;
import com.example.auth.user.service.UserService;
import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.media.Content;
import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.responses.ApiResponse;
import io.swagger.v3.oas.annotations.security.SecurityRequirement;
import io.swagger.v3.oas.annotations.tags.Tag;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.security.oauth2.jwt.Jwt;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

import java.util.UUID;

@RestController
@RequestMapping("/api/users")
@Tag(name = "Users")
@SecurityRequirement(name = OpenApiConfig.BEARER)
@ApiResponse(responseCode = "401", description = "Missing or invalid access token",
        content = @Content(schema = @Schema(implementation = ApiError.class)))
public class UserController {

    private final UserService users;

    public UserController(UserService users) {
        this.users = users;
    }

    @GetMapping("/me")
    @Operation(summary = "Current user")
    @ApiResponse(responseCode = "200", description = "The authenticated user's profile")
    public UserResponse me(@AuthenticationPrincipal Jwt jwt) {
        return users.getById(UUID.fromString(jwt.getSubject()));
    }
}
