INSERT INTO roles (id, name, description) VALUES
    (1, 'USER',  'Standard authenticated user'),
    (2, 'ADMIN', 'Administrator');

INSERT INTO permissions (id, name, description) VALUES
    (1, 'PROFILE_READ',  'Read own profile'),
    (2, 'USERS_READ',    'List and read all users'),
    (3, 'USERS_MANAGE',  'Enable, disable and lock users');

INSERT INTO role_permissions (role_id, permission_id) VALUES
    (1, 1),
    (2, 1), (2, 2), (2, 3);
