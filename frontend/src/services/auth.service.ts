import { apiRequest, publicRequest, refreshSession, setAccessToken } from '@/lib/api'
import type { AuthResponse, LoginPayload, RegisterPayload, User } from '@/types/api'

export async function register(payload: RegisterPayload): Promise<User> {
  return publicRequest<User>('/api/auth/register', { method: 'POST', body: payload })
}

export async function login(payload: LoginPayload): Promise<AuthResponse> {
  const auth = await publicRequest<AuthResponse>('/api/auth/login', { method: 'POST', body: payload })
  setAccessToken(auth.accessToken)
  return auth
}

/** Restores a session from the refresh cookie (used on page load). */
export function restoreSession(): Promise<AuthResponse> {
  return refreshSession()
}

export async function logout(): Promise<void> {
  try {
    await publicRequest<void>('/api/auth/logout', {
      method: 'POST',
      headers: { 'X-Requested-With': 'XMLHttpRequest' },
    })
  } finally {
    setAccessToken(null)
  }
}

export async function logoutAll(): Promise<void> {
  try {
    await apiRequest<void>('/api/auth/logout-all', { method: 'POST' })
  } finally {
    setAccessToken(null)
  }
}

export function getCurrentUser(): Promise<User> {
  return apiRequest<User>('/api/users/me')
}
