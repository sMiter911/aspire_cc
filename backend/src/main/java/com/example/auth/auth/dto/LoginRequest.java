package com.example.auth.auth.dto;

import io.swagger.v3.oas.annotations.media.Schema;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.Size;

public record LoginRequest(
        @NotBlank @Size(max = 254) @Schema(example = "jane@example.com") String email,
        @NotBlank @Size(max = 128) @Schema(format = "password") String password) {

    @Override
    public String toString() {
        return "LoginRequest[password=***]";
    }
}
