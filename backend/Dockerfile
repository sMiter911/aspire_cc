# syntax=docker/dockerfile:1
# Build context is the REPO ROOT (docker build -f backend/Dockerfile .): pom.xml packages ../database/migrations
# (the Flyway migrations) into the jar, so that folder must be inside the context.

# The jar is architecture independent: build on the CI host, only the runtime stage is per-architecture.
FROM --platform=$BUILDPLATFORM eclipse-temurin:25-jdk AS build
WORKDIR /src/backend
COPY backend/.mvn .mvn
COPY backend/mvnw backend/pom.xml ./
RUN sed -i 's/\r$//' mvnw && chmod +x mvnw && ./mvnw -B -ntp dependency:go-offline
COPY database/migrations /src/database/migrations
COPY backend/src src
RUN ./mvnw -B -ntp -DskipTests package && cp target/auth-*.jar /app.jar

FROM eclipse-temurin:25-jre
RUN useradd --system --uid 10001 app
USER app
COPY --from=build /app.jar /app.jar
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["java", "-XX:MaxRAMPercentage=75", "-jar", "/app.jar"]
