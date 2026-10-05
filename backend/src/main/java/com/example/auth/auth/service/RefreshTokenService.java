package com.example.auth.auth.service;

import com.example.auth.auth.entity.RefreshToken;
import com.example.auth.auth.repository.RefreshTokenRepository;
import com.example.auth.common.ClientInfo;
import com.example.auth.config.AppProperties;
import com.example.auth.exception.ApiException;
import com.example.auth.security.SecurityEvents;
import com.example.auth.user.entity.User;
import com.example.auth.user.repository.UserRepository;
import org.springframework.scheduling.annotation.Scheduled;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.security.SecureRandom;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.Base64;
import java.util.HexFormat;
import java.util.UUID;

/**
 * Refresh-token lifecycle: issue, rotate (single use), revoke, reuse detection.
 * <p>
 * Raw tokens are 256-bit random values returned once and never persisted; the database holds SHA-256 hashes.
 * A fast hash is appropriate because the input is high-entropy (not a password), and it allows an indexed lookup.
 */
@Service
public class RefreshTokenService {

    private static final SecureRandom RANDOM = new SecureRandom();

    private final RefreshTokenRepository tokens;
    private final UserRepository users;
    private final AppProperties.Refresh config;
    private final SecurityEvents events;
    private final Clock clock;

    public RefreshTokenService(RefreshTokenRepository tokens, UserRepository users, AppProperties props,
            SecurityEvents events, Clock clock) {
        this.tokens = tokens;
        this.users = users;
        this.config = props.refresh();
        this.events = events;
        this.clock = clock;
    }

    /** A raw token (for the cookie) plus the user it belongs to. */
    public record Issued(String rawToken, User user, Duration ttl) {
    }

    /** Starts a new session (family) for the user. */
    @Transactional
    public String issueForLogin(User user, ClientInfo client) {
        return persist(user.getId(), UUID.randomUUID(), client).rawToken;
    }

    /**
     * Exchanges a valid refresh token for a new one. The presented token is revoked and linked to its
     * replacement. Presenting an already-rotated token means it leaked (or a client raced itself): the whole
     * family is revoked and the change is committed even though the request fails.
     */
    @Transactional(noRollbackFor = ApiException.class)
    public Issued rotate(String rawToken, ClientInfo client) {
        Instant now = clock.instant();
        RefreshToken current = tokens.findByTokenHashForUpdate(hash(rawToken))
                .orElseThrow(ApiException::invalidRefreshToken);

        if (current.isRevoked()) {
            if (current.getReplacedByTokenId() != null) {
                tokens.revokeFamily(current.getFamilyId(), now);
                events.refreshTokenReuse(current.getUserId(), current.getFamilyId(), client.ip());
            }
            throw ApiException.invalidRefreshToken();
        }
        if (current.isExpired(now)) {
            throw ApiException.invalidRefreshToken();
        }
        User user = users.findById(current.getUserId()).orElseThrow(ApiException::invalidRefreshToken);
        if (!user.canAuthenticate(now)) {
            tokens.revokeFamily(current.getFamilyId(), now);
            throw ApiException.invalidRefreshToken();
        }

        Persisted next = persist(user.getId(), current.getFamilyId(), client);
        current.setRevokedAt(now);
        current.setReplacedByTokenId(next.entity.getId());
        events.tokenRefreshed(user.getId(), next.entity.getId());
        return new Issued(next.rawToken, user, config.ttl());
    }

    /** Ends the session the presented token belongs to. Unknown tokens are ignored (logout is idempotent). */
    @Transactional
    public void revokeSession(String rawToken) {
        tokens.findByTokenHashForUpdate(hash(rawToken)).ifPresent(t -> {
            tokens.revokeFamily(t.getFamilyId(), clock.instant());
            events.logout(t.getUserId(), t.getFamilyId());
        });
    }

    @Transactional
    public int revokeAllForUser(UUID userId) {
        int revoked = tokens.revokeAllForUser(userId, clock.instant());
        events.logoutAll(userId, revoked);
        return revoked;
    }

    /** Housekeeping: expired rows are useless (reuse of an expired token fails anyway). */
    @Scheduled(cron = "0 17 3 * * *")
    @Transactional
    public void purgeExpired() {
        tokens.deleteExpiredBefore(clock.instant().minus(Duration.ofDays(7)));
    }

    private record Persisted(RefreshToken entity, String rawToken) {
    }

    private Persisted persist(UUID userId, UUID familyId, ClientInfo client) {
        byte[] bytes = new byte[32];
        RANDOM.nextBytes(bytes);
        String raw = Base64.getUrlEncoder().withoutPadding().encodeToString(bytes);

        Instant now = clock.instant();
        RefreshToken entity = new RefreshToken();
        entity.setId(UUID.randomUUID());
        entity.setUserId(userId);
        entity.setFamilyId(familyId);
        entity.setTokenHash(hash(raw));
        entity.setCreatedAt(now);
        entity.setExpiresAt(now.plus(config.ttl()));
        entity.setIpAddress(client.ip());
        entity.setUserAgent(client.userAgent());
        tokens.saveAndFlush(entity);
        return new Persisted(entity, raw);
    }

    static String hash(String raw) {
        try {
            return HexFormat.of().formatHex(
                    MessageDigest.getInstance("SHA-256").digest(raw.getBytes(StandardCharsets.UTF_8)));
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException(e);
        }
    }
}
