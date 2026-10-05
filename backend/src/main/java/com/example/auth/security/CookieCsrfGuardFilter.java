package com.example.auth.security;

import com.example.auth.config.AppProperties;
import jakarta.servlet.FilterChain;
import jakarta.servlet.ServletException;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;
import org.springframework.web.filter.OncePerRequestFilter;

import java.io.IOException;
import java.util.Locale;
import java.util.Set;

/**
 * CSRF defence for the only endpoints that authenticate with a cookie (refresh, logout).
 * <ol>
 *   <li>SameSite=Strict on the cookie (first line of defence, see RefreshCookieFactory).</li>
 *   <li>A custom request header: cross-site HTML forms cannot send one, and cross-origin fetch/XHR that adds one
 *       triggers a CORS preflight, which only allowed origins pass.</li>
 *   <li>If an Origin header is present it must be an allowed origin.</li>
 * </ol>
 * All other endpoints use a bearer token in the Authorization header, which browsers never attach automatically.
 */
public class CookieCsrfGuardFilter extends OncePerRequestFilter {

    public static final String HEADER = "X-Requested-With";
    private static final Set<String> PROTECTED = Set.of("/api/auth/refresh", "/api/auth/logout");

    private final Set<String> allowedOrigins;
    private final ApiErrorWriter writer;
    private final SecurityEvents events;

    public CookieCsrfGuardFilter(AppProperties props, ApiErrorWriter writer, SecurityEvents events) {
        this.allowedOrigins = Set.copyOf(
                props.cors().allowedOrigins().stream().map(CookieCsrfGuardFilter::normalize).toList());
        this.writer = writer;
        this.events = events;
    }

    @Override
    protected boolean shouldNotFilter(HttpServletRequest request) {
        return !PROTECTED.contains(request.getRequestURI()) || "OPTIONS".equals(request.getMethod());
    }

    @Override
    protected void doFilterInternal(HttpServletRequest request, HttpServletResponse response, FilterChain chain)
            throws ServletException, IOException {
        String reason = null;
        if (request.getHeader(HEADER) == null) {
            reason = "missing_header";
        } else {
            String origin = request.getHeader("Origin");
            if (origin != null && !allowedOrigins.contains(normalize(origin))) {
                reason = "origin_not_allowed";
            }
        }
        if (reason != null) {
            events.csrfRejected(request.getRequestURI(), reason, request.getRemoteAddr());
            writer.write(request, response, 403, "CSRF_REJECTED", "Request rejected");
            return;
        }
        chain.doFilter(request, response);
    }

    private static String normalize(String origin) {
        String o = origin.trim().toLowerCase(Locale.ROOT);
        return o.endsWith("/") ? o.substring(0, o.length() - 1) : o;
    }
}
