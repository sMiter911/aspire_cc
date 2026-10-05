package com.example.auth.user.controller;

import com.example.auth.config.OpenApiConfig;
import com.example.auth.exception.ApiError;
import com.example.auth.user.dto.PageResponse;
import com.example.auth.user.dto.UserResponse;
import com.example.auth.user.service.UserService;
import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.media.Content;
import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.responses.ApiResponse;
import io.swagger.v3.oas.annotations.security.SecurityRequirement;
import io.swagger.v3.oas.annotations.tags.Tag;
import jakarta.validation.constraints.Max;
import jakarta.validation.constraints.Min;
import org.springframework.data.domain.PageRequest;
import org.springframework.data.domain.Sort;
import org.springframework.security.access.prepost.PreAuthorize;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

/**
 * Doubly protected: the request matcher in SecurityConfig requires ROLE_ADMIN for /api/admin/**, and
 * method security enforces it again so the rule survives a mapping/path change.
 */
@RestController
@RequestMapping("/api/admin/users")
@PreAuthorize("hasRole('ADMIN')")
@Tag(name = "Admin")
@SecurityRequirement(name = OpenApiConfig.BEARER)
@ApiResponse(responseCode = "401", description = "Missing or invalid access token",
        content = @Content(schema = @Schema(implementation = ApiError.class)))
@ApiResponse(responseCode = "403", description = "Caller is not an ADMIN",
        content = @Content(schema = @Schema(implementation = ApiError.class)))
public class AdminUserController {

    private final UserService users;

    public AdminUserController(UserService users) {
        this.users = users;
    }

    @GetMapping
    @Operation(summary = "List users (ADMIN)")
    public PageResponse<UserResponse> list(
            @RequestParam(defaultValue = "0") @Min(0) int page,
            @RequestParam(defaultValue = "20") @Min(1) @Max(100) int size) {
        return PageResponse.of(users.list(PageRequest.of(page, size, Sort.by("createdAt").descending())));
    }
}
