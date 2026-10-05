# Production Postgres image: creates the two service databases on first start (empty data directory only).
FROM postgres:18
COPY init.sql /docker-entrypoint-initdb.d/10-databases.sql
