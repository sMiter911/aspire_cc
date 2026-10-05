package com.example.auth.exception;

import com.fasterxml.jackson.annotation.JsonInclude;

import java.time.Instant;
import java.util.List;

public record ApiError(
        Instant timestamp,
        int status,
        String code,
        String message,
        String path,
        @JsonInclude(JsonInclude.Include.ALWAYS) List<FieldError> errors) {

    public record FieldError(String field, String message) {
    }

    public static ApiError of(int status, String code, String message, String path, List<FieldError> errors) {
        return new ApiError(Instant.now(), status, code, message, path, errors);
    }
}
