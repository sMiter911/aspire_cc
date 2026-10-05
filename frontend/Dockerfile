# syntax=docker/dockerfile:1
FROM node:24-alpine AS build
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

# nginx serves the SPA and is the single public origin: /api/todos and /api/admin/todos go to the Todo API,
# the rest of /api to Spring Boot. Same-origin, so the refresh cookie stays first-party and no CORS is needed.
# TODO_API_URL / BACKEND_URL are substituted into the template at container start (nginx image entrypoint).
FROM nginx:alpine
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /src/dist /usr/share/nginx/html
EXPOSE 80
