package com.example.auth.security;

import com.example.auth.exception.ApiError;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;
import org.springframework.http.MediaType;
import org.springframework.stereotype.Component;
import tools.jackson.databind.json.JsonMapper;

import java.io.IOException;
import java.util.List;

/** Writes {@link ApiError} bodies from servlet filters/handlers that run outside Spring MVC. */
@Component
public class ApiErrorWriter {

    private final JsonMapper mapper;

    public ApiErrorWriter(JsonMapper mapper) {
        this.mapper = mapper;
    }

    public void write(HttpServletRequest request, HttpServletResponse response, int status, String code,
            String message) throws IOException {
        response.setStatus(status);
        response.setContentType(MediaType.APPLICATION_JSON_VALUE);
        response.setCharacterEncoding("UTF-8");
        mapper.writeValue(response.getWriter(),
                ApiError.of(status, code, message, request.getRequestURI(), List.of()));
    }
}
