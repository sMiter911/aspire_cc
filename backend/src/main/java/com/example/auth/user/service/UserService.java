package com.example.auth.user.service;

import com.example.auth.exception.ApiException;
import com.example.auth.role.Role;
import com.example.auth.role.RoleRepository;
import com.example.auth.user.dto.UserResponse;
import com.example.auth.user.entity.User;
import com.example.auth.user.repository.UserRepository;
import org.springframework.dao.DataIntegrityViolationException;
import org.springframework.data.domain.Page;
import org.springframework.data.domain.Pageable;
import org.springframework.security.crypto.password.PasswordEncoder;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import java.text.Normalizer;
import java.util.Locale;
import java.util.UUID;

@Service
public class UserService {

    private final UserRepository users;
    private final RoleRepository roles;
    private final PasswordEncoder encoder;

    public UserService(UserRepository users, RoleRepository roles, PasswordEncoder encoder) {
        this.users = users;
        this.roles = roles;
        this.encoder = encoder;
    }

    /** Trim, Unicode-normalize (NFKC) and lower-case so "Jane@Example.com " and "jane@example.com" are one account. */
    public static String normalizeEmail(String email) {
        return Normalizer.normalize(email.trim(), Normalizer.Form.NFKC).toLowerCase(Locale.ROOT);
    }

    @Transactional
    public User register(String email, String rawPassword, String firstName, String lastName, String... roleNames) {
        String normalized = normalizeEmail(email);
        if (users.existsByEmail(normalized)) {
            throw duplicate();
        }
        User user = new User();
        user.setEmail(normalized);
        user.setPasswordHash(encoder.encode(rawPassword));
        user.setFirstName(firstName.trim());
        user.setLastName(lastName.trim());
        for (String roleName : roleNames.length == 0 ? new String[] {Role.USER} : roleNames) {
            user.getRoles().add(roles.findByName(roleName)
                    .orElseThrow(() -> new IllegalStateException("Role missing: " + roleName)));
        }
        try {
            return users.saveAndFlush(user);
        } catch (DataIntegrityViolationException e) {
            // Lost a race with a concurrent registration; the unique constraint is the real guard.
            throw duplicate();
        }
    }

    @Transactional(readOnly = true)
    public UserResponse getById(UUID id) {
        return users.findById(id).map(UserResponse::from)
                .orElseThrow(() -> new ApiException(org.springframework.http.HttpStatus.NOT_FOUND,
                        "USER_NOT_FOUND", "User not found"));
    }

    @Transactional(readOnly = true)
    public Page<UserResponse> list(Pageable pageable) {
        return users.findAll(pageable).map(UserResponse::from);
    }

    private static ApiException duplicate() {
        return ApiException.conflict("EMAIL_ALREADY_REGISTERED", "An account with this email already exists");
    }
}
