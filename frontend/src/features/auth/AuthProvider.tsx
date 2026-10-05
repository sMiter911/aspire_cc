import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { ApiError, onSessionChange, setAccessToken } from '@/lib/api'
import * as authService from '@/services/auth.service'
import type { AuthResponse, LoginPayload, User } from '@/types/api'
import { AuthContext, type AuthContextValue, type AuthStatus } from './auth-context'

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('loading')
  const [user, setUser] = useState<User | null>(null)
  const [sessionExpired, setSessionExpired] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const wasAuthenticated = useRef(false)

  const applySession = useCallback((auth: AuthResponse | null) => {
    if (auth) {
      wasAuthenticated.current = true
      setUser(auth.user)
      setStatus('authenticated')
      setSessionExpired(false)
      setError(null)
    } else {
      // A refresh failed while we thought we were signed in: the session expired or was revoked.
      if (wasAuthenticated.current) setSessionExpired(true)
      wasAuthenticated.current = false
      setUser(null)
      setStatus('unauthenticated')
    }
  }, [])

  useEffect(() => {
    onSessionChange(applySession)
    return () => onSessionChange(null)
  }, [applySession])

  // Session restoration: on first load try to turn the refresh cookie into an access token.
  useEffect(() => {
    let cancelled = false
    authService
      .restoreSession()
      .then((auth) => {
        if (!cancelled) applySession(auth)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        if (err instanceof ApiError && err.status === 401) {
          applySession(null) // no session: normal for first-time visitors
          return
        }
        setError(err instanceof ApiError ? err.message : 'Unable to restore your session')
        setStatus('unauthenticated')
      })
    return () => {
      cancelled = true
    }
  }, [applySession])

  const login = useCallback(
    async (payload: LoginPayload) => {
      const auth = await authService.login(payload) // throws ApiError for the form to display
      applySession(auth)
    },
    [applySession],
  )

  const logout = useCallback(async () => {
    try {
      await authService.logout()
    } finally {
      wasAuthenticated.current = false
      setAccessToken(null)
      setUser(null)
      setStatus('unauthenticated')
      setSessionExpired(false)
    }
  }, [])

  const value = useMemo<AuthContextValue>(
    () => ({
      status,
      user,
      sessionExpired,
      error,
      login,
      logout,
      hasRole: (role) => user?.roles.includes(role) ?? false,
      clearSessionExpired: () => setSessionExpired(false),
    }),
    [status, user, sessionExpired, error, login, logout],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
