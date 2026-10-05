-- Users -------------------------------------------------------------------
CREATE TABLE users (
    id                     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    email                  VARCHAR(254) NOT NULL,          -- stored normalized (trimmed, lower-case)
    password_hash          VARCHAR(255) NOT NULL,          -- Argon2id encoded hash
    first_name             VARCHAR(100) NOT NULL,
    last_name              VARCHAR(100) NOT NULL,
    enabled                BOOLEAN      NOT NULL DEFAULT TRUE,
    account_locked         BOOLEAN      NOT NULL DEFAULT FALSE,  -- manual/administrative lock
    failed_login_attempts  INTEGER      NOT NULL DEFAULT 0,
    lockout_until          TIMESTAMPTZ,                          -- temporary brute-force lockout
    created_at             TIMESTAMPTZ  NOT NULL DEFAULT now(),
    updated_at             TIMESTAMPTZ  NOT NULL DEFAULT now(),
    last_login_at          TIMESTAMPTZ,
    CONSTRAINT uq_users_email UNIQUE (email)
);

-- Roles & permissions -------------------------------------------------------
CREATE TABLE roles (
    id          SMALLINT PRIMARY KEY,
    name        VARCHAR(50)  NOT NULL UNIQUE,
    description VARCHAR(255)
);

CREATE TABLE permissions (
    id          SMALLINT PRIMARY KEY,
    name        VARCHAR(100) NOT NULL UNIQUE,
    description VARCHAR(255)
);

CREATE TABLE role_permissions (
    role_id       SMALLINT NOT NULL REFERENCES roles (id) ON DELETE CASCADE,
    permission_id SMALLINT NOT NULL REFERENCES permissions (id) ON DELETE CASCADE,
    PRIMARY KEY (role_id, permission_id)
);

CREATE TABLE user_roles (
    user_id UUID     NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    role_id SMALLINT NOT NULL REFERENCES roles (id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, role_id)
);
CREATE INDEX idx_user_roles_role_id ON user_roles (role_id);

-- Refresh tokens (one row per issued token; a login starts a new "family") --------
CREATE TABLE refresh_tokens (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),   -- token identifier (jti-like)
    user_id               UUID         NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    family_id             UUID         NOT NULL,                        -- all rotations of one session share this
    token_hash            VARCHAR(64)  NOT NULL,                        -- hex SHA-256 of the raw token; raw never stored
    expires_at            TIMESTAMPTZ  NOT NULL,
    created_at            TIMESTAMPTZ  NOT NULL DEFAULT now(),
    revoked_at            TIMESTAMPTZ,
    replaced_by_token_id  UUID REFERENCES refresh_tokens (id),
    ip_address            VARCHAR(45),
    user_agent            VARCHAR(512),
    CONSTRAINT uq_refresh_tokens_token_hash UNIQUE (token_hash)
);
CREATE INDEX idx_refresh_tokens_user_id   ON refresh_tokens (user_id);
CREATE INDEX idx_refresh_tokens_family_id ON refresh_tokens (family_id);
CREATE INDEX idx_refresh_tokens_expires_at ON refresh_tokens (expires_at);
