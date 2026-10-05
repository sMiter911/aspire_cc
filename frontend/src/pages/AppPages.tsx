import { Link } from 'react-router'
import { toast } from 'sonner'
import { FormAlert } from '@/components/FormAlert'
import { Badge } from '@/components/ui/badge'
import { Button, buttonVariants } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { RequireRole } from '@/components/RequireAuthorization'
import { useAuth } from '@/hooks/useAuth'
import { useQuery } from '@/hooks/useQuery'
import { todoService } from '@/services/todo.service'
import { ApiError } from '@/lib/api'
import { cn } from '@/lib/utils'
import * as adminService from '@/services/admin.service'
import * as authService from '@/services/auth.service'
import { useNavigate } from 'react-router'

const fmt = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : 'never')

export function DashboardPage() {
  const { user } = useAuth()
  const open = useQuery(() => todoService.list({ completed: false, size: 1 }))
  return (
    <div className="grid gap-6">
      <div>
        <h1 className="text-2xl font-semibold">Hello, {user?.firstName}</h1>
        <p className="text-muted-foreground">You are signed in.</p>
      </div>
      <div className="grid gap-4 sm:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>My todos</CardTitle>
            <CardDescription>
              {open.data ? `${open.data.totalElements} open` : open.error ? 'Unavailable' : 'Loading...'}
            </CardDescription>
          </CardHeader>
          <CardContent>
            <Link to="/todos" className={buttonVariants({ variant: 'outline' })}>
              Open my todos
            </Link>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Your roles</CardTitle>
            <CardDescription>What you are allowed to do</CardDescription>
          </CardHeader>
          <CardContent className="flex flex-wrap gap-2">
            {user?.roles.map((r) => (
              <Badge key={r}>{r}</Badge>
            ))}
          </CardContent>
        </Card>
        <RequireRole role="ADMIN">
          <Card>
            <CardHeader>
              <CardTitle>Administration</CardTitle>
              <CardDescription>Visible to administrators only</CardDescription>
            </CardHeader>
            <CardContent className="flex gap-2">
              <Link to="/admin/todos" className={buttonVariants({ variant: 'outline' })}>
                All todos
              </Link>
              <Link to="/admin/users" className={buttonVariants({ variant: 'outline' })}>
                Users
              </Link>
            </CardContent>
          </Card>
        </RequireRole>
      </div>
    </div>
  )
}

export function ProfilePage() {
  const navigate = useNavigate()
  const { logout } = useAuth()
  // Re-fetch from the API (rather than trusting cached login data) to demonstrate an authenticated call.
  const { data: user, error, loading } = useQuery(authService.getCurrentUser)

  const signOutEverywhere = async () => {
    try {
      await authService.logoutAll()
      await logout()
      toast.success('Signed out of all devices')
      navigate('/login', { replace: true })
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Could not sign out of all devices')
    }
  }

  return (
    <Card className="max-w-xl">
      <CardHeader>
        <CardTitle>Profile</CardTitle>
        <CardDescription>Your account details</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {loading && <Skeleton className="h-24 w-full" />}
        {error && <FormAlert title="Could not load profile" message={error} />}
        {user && (
          <dl className="grid grid-cols-[8rem_1fr] gap-y-2 text-sm">
            <dt className="text-muted-foreground">Name</dt>
            <dd>
              {user.firstName} {user.lastName}
            </dd>
            <dt className="text-muted-foreground">Email</dt>
            <dd>{user.email}</dd>
            <dt className="text-muted-foreground">Roles</dt>
            <dd className="flex gap-1">{user.roles.map((r) => <Badge key={r} variant="secondary">{r}</Badge>)}</dd>
            <dt className="text-muted-foreground">Member since</dt>
            <dd>{fmt(user.createdAt)}</dd>
            <dt className="text-muted-foreground">Last sign-in</dt>
            <dd>{fmt(user.lastLoginAt)}</dd>
          </dl>
        )}
        <div>
          <Button variant="outline" onClick={signOutEverywhere}>
            Sign out of all devices
          </Button>
        </div>
      </CardContent>
    </Card>
  )
}

export function AdminUsersPage() {
  const { data, error, loading } = useQuery(() => adminService.listUsers(0, 50))
  return (
    <Card>
      <CardHeader>
        <CardTitle>Users</CardTitle>
        <CardDescription>All registered accounts (ADMIN only)</CardDescription>
      </CardHeader>
      <CardContent>
        {loading && <Skeleton className="h-40 w-full" />}
        {error && <FormAlert title="Could not load users" message={error} />}
        {data && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Email</TableHead>
                <TableHead>Roles</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Last sign-in</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.content.map((u) => (
                <TableRow key={u.id}>
                  <TableCell>
                    {u.firstName} {u.lastName}
                  </TableCell>
                  <TableCell>{u.email}</TableCell>
                  <TableCell className="space-x-1">
                    {u.roles.map((r) => (
                      <Badge key={r} variant={r === 'ADMIN' ? 'default' : 'secondary'}>
                        {r}
                      </Badge>
                    ))}
                  </TableCell>
                  <TableCell>{u.enabled ? 'Active' : 'Disabled'}</TableCell>
                  <TableCell>{fmt(u.lastLoginAt)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
        {data && <p className="mt-3 text-xs text-muted-foreground">{data.totalElements} users</p>}
      </CardContent>
    </Card>
  )
}

export function ForbiddenPage() {
  return (
    <div className="mx-auto grid max-w-md gap-4 py-24 text-center">
      <h1 className="text-2xl font-semibold">403 - Not authorized</h1>
      <p className="text-muted-foreground">Your account does not have permission to view this page.</p>
      <Link to="/dashboard" className={cn(buttonVariants({ variant: 'outline' }), 'mx-auto')}>
        Back to dashboard
      </Link>
    </div>
  )
}

export function NotFoundPage() {
  return (
    <div className="mx-auto grid max-w-md gap-4 py-24 text-center">
      <h1 className="text-2xl font-semibold">Page not found</h1>
      <Link to="/dashboard" className={cn(buttonVariants({ variant: 'outline' }), 'mx-auto')}>
        Go home
      </Link>
    </div>
  )
}
