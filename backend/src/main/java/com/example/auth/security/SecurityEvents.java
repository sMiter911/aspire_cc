package com.example.auth.security;

import lombok.extern.slf4j.Slf4j;
import org.springframework.stereotype.Component;

import java.util.UUID;

/**
 * Audit-style log lines for security-relevant events. Only identifiers are logged, never passwords, tokens,
 * or raw emails of unknown accounts (those would let an attacker poison/read the log).
 */
@Slf4j
@Component
public class SecurityEvents {

    public void registered(UUID userId) {
        log.info("security_event=REGISTERED userId={}", userId);
    }

    public void loginSuccess(UUID userId, String ip) {
        log.info("security_event=LOGIN_SUCCESS userId={} ip={}", userId, ip);
    }

    public void loginFailure(UUID userIdOrNull, String reason, String ip) {
        log.warn("security_event=LOGIN_FAILURE userId={} reason={} ip={}", userIdOrNull, reason, ip);
    }

    public void accountLockedOut(UUID userId) {
        log.warn("security_event=ACCOUNT_LOCKOUT userId={}", userId);
    }

    public void tokenRefreshed(UUID userId, UUID tokenId) {
        log.info("security_event=TOKEN_REFRESHED userId={} tokenId={}", userId, tokenId);
    }

    public void refreshTokenReuse(UUID userId, UUID familyId, String ip) {
        log.warn("security_event=REFRESH_TOKEN_REUSE_DETECTED userId={} familyId={} ip={} action=family_revoked",
                userId, familyId, ip);
    }

    public void logout(UUID userId, UUID familyId) {
        log.info("security_event=LOGOUT userId={} familyId={}", userId, familyId);
    }

    public void logoutAll(UUID userId, int revoked) {
        log.info("security_event=LOGOUT_ALL userId={} revokedTokens={}", userId, revoked);
    }

    public void rateLimited(String policy, String ip) {
        log.warn("security_event=RATE_LIMITED policy={} ip={}", policy, ip);
    }

    public void csrfRejected(String path, String reason, String ip) {
        log.warn("security_event=CSRF_REJECTED path={} reason={} ip={}", path, reason, ip);
    }
}
