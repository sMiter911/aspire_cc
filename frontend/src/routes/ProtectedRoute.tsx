import { Navigate, Outlet, useLocation } from 'react-router'
import { FullPageSpinner } from '@/components/FullPageSpinner'
import { useAuth } from '@/hooks/useAuth'
import { useAuthorization } from '@/hooks/useAuthorization'
import type { Capability } from '@/lib/authorization'

/**
 * Gate for signed-in pages. Optionally also requires a role or a capability (UX only: the APIs enforce it again).
 * States: loading -> spinner, unauthenticated -> /login (remembering where we were), not allowed -> /forbidden.
 */
export function ProtectedRoute({ role, permission }: { role?: string; permission?: Capability }) {
  const { status, sessionExpired } = useAuth()
  const { hasRole, can } = useAuthorization()
  const location = useLocation()

  if (status === 'loading') return <FullPageSpinner />
  if (status === 'unauthenticated') {
    return <Navigate to="/login" replace state={{ from: location.pathname, expired: sessionExpired }} />
  }
  if ((role && !hasRole(role)) || (permission && !can(permission))) return <Navigate to="/forbidden" replace />
  return <Outlet />
}
