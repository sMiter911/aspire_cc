import { useMemo } from 'react'
import { useAuth } from '@/hooks/useAuth'
import { capabilitiesFor, type Capability } from '@/lib/authorization'

export function useAuthorization() {
  const { user, status } = useAuth()
  return useMemo(() => {
    const roles = user?.roles ?? []
    const caps = capabilitiesFor(roles)
    return {
      isAuthenticated: status === 'authenticated',
      roles,
      hasRole: (role: string) => roles.includes(role),
      hasAnyRole: (...wanted: string[]) => wanted.some((r) => roles.includes(r)),
      can: (capability: Capability) => caps.has(capability),
    }
  }, [user, status])
}
