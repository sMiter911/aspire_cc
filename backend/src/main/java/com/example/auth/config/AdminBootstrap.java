package com.example.auth.config;

import com.example.auth.role.Role;
import com.example.auth.user.repository.UserRepository;
import com.example.auth.user.service.UserService;
import lombok.extern.slf4j.Slf4j;
import org.springframework.boot.ApplicationArguments;
import org.springframework.boot.ApplicationRunner;
import org.springframework.stereotype.Component;
import org.springframework.util.StringUtils;

/**
 * Creates the first ADMIN when APP_BOOTSTRAP_ADMIN_EMAIL / APP_BOOTSTRAP_ADMIN_PASSWORD are provided and no
 * account with that email exists. There is deliberately no default admin and no admin self-registration.
 */
@Slf4j
@Component
public class AdminBootstrap implements ApplicationRunner {

    private final AppProperties.BootstrapAdmin config;
    private final UserService userService;
    private final UserRepository users;

    public AdminBootstrap(AppProperties props, UserService userService, UserRepository users) {
        this.config = props.bootstrapAdmin();
        this.userService = userService;
        this.users = users;
    }

    @Override
    public void run(ApplicationArguments args) {
        if (config == null || !StringUtils.hasText(config.email()) || !StringUtils.hasText(config.password())) {
            return;
        }
        if (users.existsByEmail(UserService.normalizeEmail(config.email()))) {
            return;
        }
        var admin = userService.register(config.email(), config.password(), "Admin", "User", Role.USER, Role.ADMIN);
        log.info("Bootstrap admin account created userId={}", admin.getId());
    }
}
