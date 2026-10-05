package com.example.auth.auth.dto;

import io.swagger.v3.oas.annotations.media.Schema;
import jakarta.validation.constraints.Email;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.Size;

public record RegisterRequest(
        @NotBlank @Email @Size(max = 254) @Schema(example = "jane@example.com") String email,
        @NotBlank @StrongPassword @Schema(example = "Correct-Horse-Battery-9", format = "password") String password,
        @NotBlank @Size(max = 100) @Schema(example = "Jane") String firstName,
        @NotBlank @Size(max = 100) @Schema(example = "Doe") String lastName) {

    /** Surrounding whitespace on the email is a typo, not part of the address. */
    public RegisterRequest {
        email = email == null ? null : email.trim();
    }

    /** Keep secrets out of logs and exception messages. */
    @Override
    public String toString() {
        return "RegisterRequest[email=" + email + ", password=***]";
    }
}
