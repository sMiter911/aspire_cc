import type { ReactNode } from 'react'
import { useAuthorization } from '@/hooks/useAuthorization'
import type { Capability } from '@/lib/authorization'

/** Render children only for users holding one of the given roles. UX only; the API enforces the real rule. */
export function RequireRole({
  role,
  roles,
  fallback = null,
  children,
}: {
  role?: string
  roles?: string[]
  fallback?: ReactNode
  children: ReactNode
}) {
  const { hasAnyRole } = useAuthorization()
  const wanted = roles ?? (role ? [role] : [])
  return hasAnyRole(...wanted) ? <>{children}</> : <>{fallback}</>
}

/** Render children only when the user has the given capability (derived centrally from roles). */
export function RequirePermission({
  permission,
  fallback = null,
  children,
}: {
  permission: Capability
  fallback?: ReactNode
  children: ReactNode
}) {
  const { can } = useAuthorization()
  return can(permission) ? <>{children}</> : <>{fallback}</>
}
