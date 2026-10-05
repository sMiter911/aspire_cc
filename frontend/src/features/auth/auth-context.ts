import { createContext } from 'react'
import type { LoginPayload, User } from '@/types/api'

export type AuthStatus = 'loading' | 'authenticated' | 'unauthenticated'

export interface AuthContextValue {
  status: AuthStatus
  user: User | null
  /** Set when the previously authenticated session could no longer be refreshed. */
  sessionExpired: boolean
  /** Non-credential failures while restoring a session (e.g. API unreachable). */
  error: string | null
  login: (payload: LoginPayload) => Promise<void>
  logout: () => Promise<void>
  hasRole: (role: string) => boolean
  clearSessionExpired: () => void
}

export const AuthContext = createContext<AuthContextValue | null>(null)
