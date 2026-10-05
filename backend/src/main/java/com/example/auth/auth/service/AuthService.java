package com.example.auth.auth.service;

import com.example.auth.auth.dto.AuthResponse;
import com.example.auth.auth.dto.LoginRequest;
import com.example.auth.auth.dto.RegisterRequest;
import com.example.auth.common.ClientInfo;
import com.example.auth.config.AppProperties;
import com.example.auth.exception.ApiException;
import com.example.auth.security.JwtService;
import com.example.auth.security.RateLimitService;
import com.example.auth.security.SecurityEvents;
import com.example.auth.user.dto.UserResponse;
import com.example.auth.user.entity.User;
import com.example.auth.user.repository.UserRepository;
import com.example.auth.user.service.UserService;
import org.springframework.security.crypto.password.PasswordEncoder;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import java.time.Clock;
import java.time.Instant;
import java.util.UUID;

@Service
public class AuthService {

    private final UserService userService;
    private final UserRepository users;
    private final PasswordEncoder encoder;
    private final JwtService jwt;
    private final RefreshTokenService refreshTokens;
    private final RateLimitService rateLimiter;
    private final SecurityEvents events;
    private final AppProperties.Lockout lockout;
    private final Clock clock;
    /** Verified against when the email is unknown so response time does not reveal whether an account exists. */
    private final String dummyHash;

    public AuthService(UserService userService, UserRepository users, PasswordEncoder encoder, JwtService jwt,
            RefreshTokenService refreshTokens, RateLimitService rateLimiter, SecurityEvents events,
            AppProperties props, Clock clock) {
        this.userService = userService;
        this.users = users;
        this.encoder = encoder;
        this.jwt = jwt;
        this.refreshTokens = refreshTokens;
        this.rateLimiter = rateLimiter;
        this.events = events;
        this.lockout = props.lockout();
        this.clock = clock;
        this.dummyHash = encoder.encode(UUID.randomUUID().toString());
    }

    /** Result of login/refresh: JSON body plus the raw refresh token destined for the cookie. */
    public record Session(AuthResponse response, String refreshToken) {
    }

    public UserResponse register(RegisterRequest request, ClientInfo client) {
        rateLimiter.check("register", rateLimiter.registerPolicy(), client.ip(), client.ip());
        User user = userService.register(request.email(), request.password(), request.firstName(),
                request.lastName());
        events.registered(user.getId());
        return UserResponse.from(user);
    }

    /**
     * Failed-attempt bookkeeping must commit even though we throw, hence noRollbackFor.
     * Every failure path returns the same {@link ApiException#invalidCredentials()}.
     */
    @Transactional(noRollbackFor = ApiException.class)
    public Session login(LoginRequest request, ClientInfo client) {
        String email = UserService.normalizeEmail(request.email());
        // Two buckets: per source IP (credential stuffing) and per target account (targeted guessing).
        rateLimiter.check("login-ip", rateLimiter.loginPolicy(), client.ip(), client.ip());
        rateLimiter.check("login-email", rateLimiter.loginPolicy(), email, client.ip());

        Instant now = clock.instant();
        User user = users.findByEmail(email).orElse(null);
        if (user == null) {
            encoder.matches(request.password(), dummyHash);
            events.loginFailure(null, "unknown_user", client.ip());
            throw ApiException.invalidCredentials();
        }
        if (!user.canAuthenticate(now)) {
            encoder.matches(request.password(), dummyHash);
            events.loginFailure(user.getId(), "account_unavailable", client.ip());
            throw ApiException.invalidCredentials();
        }
        if (!encoder.matches(request.password(), user.getPasswordHash())) {
            registerFailure(user, now, client);
            throw ApiException.invalidCredentials();
        }

        user.setFailedLoginAttempts(0);
        user.setLockoutUntil(null);
        user.setLastLoginAt(now);
        events.loginSuccess(user.getId(), client.ip());
        String refresh = refreshTokens.issueForLogin(user, client);
        return new Session(authResponse(user), refresh);
    }

    public Session refresh(String rawRefreshToken, ClientInfo client) {
        rateLimiter.check("refresh-ip", rateLimiter.refreshPolicy(), client.ip(), client.ip());
        if (rawRefreshToken == null || rawRefreshToken.isBlank()) {
            throw ApiException.invalidRefreshToken();
        }
        RefreshTokenService.Issued issued = refreshTokens.rotate(rawRefreshToken, client);
        return new Session(authResponse(issued.user()), issued.rawToken());
    }

    public void logout(String rawRefreshToken) {
        if (rawRefreshToken != null && !rawRefreshToken.isBlank()) {
            refreshTokens.revokeSession(rawRefreshToken);
        }
    }

    public void logoutAll(UUID userId) {
        refreshTokens.revokeAllForUser(userId);
    }

    private void registerFailure(User user, Instant now, ClientInfo client) {
        int attempts = user.getFailedLoginAttempts() + 1;
        events.loginFailure(user.getId(), "bad_password", client.ip());
        if (attempts >= lockout.maxFailedAttempts()) {
            user.setFailedLoginAttempts(0);
            user.setLockoutUntil(now.plus(lockout.duration()));
            events.accountLockedOut(user.getId());
        } else {
            user.setFailedLoginAttempts(attempts);
        }
    }

    private AuthResponse authResponse(User user) {
        return AuthResponse.bearer(jwt.createAccessToken(user), jwt.accessTtl().toSeconds(),
                UserResponse.from(user));
    }
}
