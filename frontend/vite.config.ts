/// <reference types="vitest/config" />
import path from 'node:path'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The browser only ever talks to its own origin. Vite proxies /api to the Spring Boot service, so the
// refresh cookie is first-party and no CORS is involved in development. Aspire injects BACKEND_URL; the
// fallback is only for running `npm run dev` without Aspire.
const backendUrl = process.env.BACKEND_URL ?? 'http://localhost:8080'
const todoApiUrl = process.env.TODO_API_URL ?? 'http://localhost:5229'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(import.meta.dirname, './src') },
  },
  server: {
    port: Number(process.env.PORT) || 5173,
    host: true,
    proxy: {
      // Order matters: todo routes go to the ASP.NET Core Todo API, everything else under /api to Spring Boot
      // (identity). Both stay same-origin for the browser, so no CORS is involved in development.
      '^/api/(todos|admin/todos)': { target: todoApiUrl, changeOrigin: false },
      '/api': { target: backendUrl, changeOrigin: false },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
})
