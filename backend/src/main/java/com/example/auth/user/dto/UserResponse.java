package com.example.auth.user.dto;

import com.example.auth.role.Permission;
import com.example.auth.role.Role;
import com.example.auth.user.entity.User;
import io.swagger.v3.oas.annotations.media.Schema;

import java.time.Instant;
import java.util.Set;
import java.util.TreeSet;
import java.util.UUID;

/** Public view of a user. Deliberately omits password hash, lockout and failed-attempt counters. */
public record UserResponse(
        UUID id,
        String email,
        String firstName,
        String lastName,
        @Schema(example = "[\"USER\"]") Set<String> roles,
        @Schema(example = "[\"PROFILE_READ\"]") Set<String> permissions,
        boolean enabled,
        Instant createdAt,
        Instant lastLoginAt) {

    public static UserResponse from(User user) {
        Set<String> roles = new TreeSet<>();
        Set<String> permissions = new TreeSet<>();
        for (Role role : user.getRoles()) {
            roles.add(role.getName());
            for (Permission permission : role.getPermissions()) {
                permissions.add(permission.getName());
            }
        }
        return new UserResponse(user.getId(), user.getEmail(), user.getFirstName(), user.getLastName(),
                roles, permissions, user.isEnabled(), user.getCreatedAt(), user.getLastLoginAt());
    }
}
