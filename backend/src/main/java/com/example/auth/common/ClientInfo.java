package com.example.auth.common;

import jakarta.servlet.http.HttpServletRequest;

/**
 * Caller metadata used for rate limiting and session records. The remote address is only the real client IP when
 * the app is reached directly or a trusted proxy is configured via server.forward-headers-strategy.
 */
public record ClientInfo(String ip, String userAgent) {

    private static final int MAX_UA = 512;

    public static ClientInfo from(HttpServletRequest request) {
        String ua = request.getHeader("User-Agent");
        if (ua != null && ua.length() > MAX_UA) {
            ua = ua.substring(0, MAX_UA);
        }
        return new ClientInfo(request.getRemoteAddr(), ua);
    }
}
