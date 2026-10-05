import type { ApiErrorBody, AuthResponse, FieldError } from '@/types/api'

/**
 * Base URL of the API. Empty means "same origin": in development Vite proxies /api to Spring Boot (see
 * vite.config.ts); in production put both behind one host or set VITE_API_URL at build time.
 */
const API_BASE: string = import.meta.env.VITE_API_URL ?? ''

export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly errors: FieldError[]

  constructor(status: number, code: string, message: string, errors: FieldError[] = []) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.errors = errors
  }
}

/**
 * The access token lives only in this module's memory: never localStorage/sessionStorage (readable by any
 * injected script) and never a cookie. A page reload loses it; the HttpOnly refresh cookie restores it.
 */
let accessToken: string | null = null
let sessionListener: ((auth: AuthResponse | null) => void) | null = null

export function setAccessToken(token: string | null) {
  accessToken = token
}

export function getAccessToken() {
  return accessToken
}

/** AuthProvider subscribes so a silent refresh (or its failure) updates React state. */
export function onSessionChange(listener: ((auth: AuthResponse | null) => void) | null) {
  sessionListener = listener
}

let refreshInFlight: Promise<AuthResponse> | null = null

/**
 * Exchanges the refresh cookie for a new access token. Single-flight: refresh tokens rotate and are one-time
 * use, so concurrent 401s must share one request or the second would be treated as token reuse.
 */
export function refreshSession(): Promise<AuthResponse> {
  refreshInFlight ??= send<AuthResponse>('/api/auth/refresh', {
    method: 'POST',
    // Custom header = CSRF defence required by the API for cookie-authenticated endpoints.
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
    skipAuth: true,
  })
    .then((auth) => {
      accessToken = auth.accessToken
      sessionListener?.(auth)
      return auth
    })
    .catch((err: unknown) => {
      accessToken = null
      if (err instanceof ApiError && err.status === 401) sessionListener?.(null)
      throw err
    })
    .finally(() => {
      refreshInFlight = null
    })
  return refreshInFlight
}

interface SendOptions {
  method?: string
  body?: unknown
  headers?: Record<string, string>
  skipAuth?: boolean
}

async function send<T>(path: string, opts: SendOptions = {}): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json', ...opts.headers }
  if (opts.body !== undefined) headers['Content-Type'] = 'application/json'
  if (!opts.skipAuth && accessToken) headers.Authorization = `Bearer ${accessToken}`

  let res: Response
  try {
    res = await fetch(`${API_BASE}${path}`, {
      method: opts.method ?? 'GET',
      headers,
      body: opts.body === undefined ? undefined : JSON.stringify(opts.body),
      credentials: 'include', // send/receive the HttpOnly refresh cookie
    })
  } catch {
    throw new ApiError(0, 'NETWORK_ERROR', 'Unable to reach the server. Check your connection and try again.')
  }

  if (res.status === 204) return undefined as T
  const text = await res.text()
  const data: unknown = text ? JSON.parse(text) : undefined
  if (!res.ok) {
    const body = (data ?? {}) as Partial<ApiErrorBody>
    throw new ApiError(res.status, body.code ?? 'UNKNOWN_ERROR', body.message ?? 'Something went wrong', body.errors ?? [])
  }
  return data as T
}

/** Authenticated request: on 401 refresh once and replay; if that fails the session is over. */
export async function apiRequest<T>(path: string, opts: Omit<SendOptions, 'skipAuth'> = {}): Promise<T> {
  try {
    return await send<T>(path, opts)
  } catch (err) {
    if (err instanceof ApiError && err.status === 401) {
      await refreshSession() // throws (and signals session expiry) if the refresh token is no longer valid
      return send<T>(path, opts)
    }
    throw err
  }
}

/** Unauthenticated request (login/register/logout). */
export function publicRequest<T>(path: string, opts: Omit<SendOptions, 'skipAuth'> = {}): Promise<T> {
  return send<T>(path, { ...opts, skipAuth: true })
}
