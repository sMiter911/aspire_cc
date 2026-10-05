import { Navigate, Outlet } from 'react-router'
import { FullPageSpinner } from '@/components/FullPageSpinner'
import { useAuth } from '@/hooks/useAuth'

/** Login/register pages bounce signed-in users to the dashboard. */
export function GuestRoute() {
  const { status } = useAuth()
  if (status === 'loading') return <FullPageSpinner />
  if (status === 'authenticated') return <Navigate to="/dashboard" replace />
  return <Outlet />
}
