package com.example.auth.security;

import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;
import org.springframework.security.core.AuthenticationException;
import org.springframework.security.web.AuthenticationEntryPoint;
import org.springframework.stereotype.Component;

import java.io.IOException;

/** 401 for missing, malformed, expired or otherwise invalid bearer tokens. Cause is intentionally not disclosed. */
@Component
public class RestAuthenticationEntryPoint implements AuthenticationEntryPoint {

    private final ApiErrorWriter writer;

    public RestAuthenticationEntryPoint(ApiErrorWriter writer) {
        this.writer = writer;
    }

    @Override
    public void commence(HttpServletRequest request, HttpServletResponse response, AuthenticationException ex)
            throws IOException {
        response.setHeader("WWW-Authenticate", "Bearer");
        writer.write(request, response, 401, "UNAUTHENTICATED", "Authentication required");
    }
}
