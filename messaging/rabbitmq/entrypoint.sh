#!/bin/bash
# Builds /etc/rabbitmq/definitions.json = committed topology + broker user. RabbitMQ stores a password as
# base64(salt(4 bytes) || sha256(salt || password)).
set -euo pipefail

: "${RABBITMQ_DEFAULT_USER:?RABBITMQ_DEFAULT_USER is required}"
: "${RABBITMQ_DEFAULT_PASS:?RABBITMQ_DEFAULT_PASS is required}"

salt=$(mktemp); openssl rand 4 > "$salt"
hash=$({ cat "$salt"; printf '%s' "$RABBITMQ_DEFAULT_PASS"; } | openssl dgst -sha256 -binary | { cat "$salt" -; } | base64 -w0)
rm -f "$salt"

# Inject users + permissions right after the opening brace of the topology file.
head='{"users":[{"name":"'"$RABBITMQ_DEFAULT_USER"'","password_hash":"'"$hash"'","hashing_algorithm":"rabbit_password_hashing_sha256","tags":"administrator"}],"permissions":[{"user":"'"$RABBITMQ_DEFAULT_USER"'","vhost":"/","configure":".*","write":".*","read":".*"}],'
{ printf '%s' "$head"; sed '0,/^{/s///' /etc/rabbitmq/topology.json; } > /etc/rabbitmq/definitions.json
chown rabbitmq:rabbitmq /etc/rabbitmq/definitions.json
chmod 600 /etc/rabbitmq/definitions.json

exec docker-entrypoint.sh "$@"
