package com.example.auth.auth.service;

import com.example.auth.config.AppProperties;
import org.springframework.http.ResponseCookie;
import org.springframework.stereotype.Component;

import java.time.Duration;

/**
 * Builds the refresh-token cookie: HttpOnly (invisible to JavaScript, limits XSS theft), Secure (HTTPS only),
 * SameSite=Strict (never sent on cross-site requests), Path=/api/auth (not sent with ordinary API calls).
 */
@Component
public class RefreshCookieFactory {

    private final AppProperties.Refresh config;

    public RefreshCookieFactory(AppProperties props) {
        this.config = props.refresh();
    }

    public String cookieName() {
        return config.cookieName();
    }

    public ResponseCookie create(String rawToken) {
        return base(rawToken).maxAge(config.ttl()).build();
    }

    public ResponseCookie clear() {
        return base("").maxAge(Duration.ZERO).build();
    }

    private ResponseCookie.ResponseCookieBuilder base(String value) {
        return ResponseCookie.from(config.cookieName(), value)
                .httpOnly(true)
                .secure(config.cookieSecure())
                .sameSite(config.cookieSameSite())
                .path(config.cookiePath());
    }
}
