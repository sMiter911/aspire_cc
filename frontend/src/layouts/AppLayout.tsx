import { ChevronDownIcon, LogOutIcon, ShieldIcon } from 'lucide-react'
import { NavLink, Outlet, useNavigate } from 'react-router'
import { Avatar, AvatarFallback } from '@/components/ui/avatar'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuGroup,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { RequireRole } from '@/components/RequireAuthorization'
import { useAuth } from '@/hooks/useAuth'
import { cn } from '@/lib/utils'

const linkClass = ({ isActive }: { isActive: boolean }) =>
  cn('text-sm transition-colors hover:text-foreground', isActive ? 'font-medium text-foreground' : 'text-muted-foreground')

export function AppLayout() {
  const { user, logout } = useAuth()
  const navigate = useNavigate()
  const initials = user ? `${user.firstName[0] ?? ''}${user.lastName[0] ?? ''}`.toUpperCase() : ''

  const onLogout = async () => {
    await logout()
    navigate('/login', { replace: true })
  }

  return (
    <div className="min-h-svh">
      <header className="border-b">
        <div className="mx-auto flex h-14 max-w-5xl items-center justify-between px-4">
          <nav className="flex items-center gap-6" aria-label="Main">
            <span className="flex items-center gap-2 font-semibold">
              <ShieldIcon className="size-4" /> Auth Starter
            </span>
            <NavLink to="/dashboard" className={linkClass}>
              Dashboard
            </NavLink>
            <NavLink to="/todos" className={linkClass}>
              Todos
            </NavLink>
            <NavLink to="/profile" className={linkClass}>
              Profile
            </NavLink>
            <RequireRole role="ADMIN">
              <NavLink to="/admin/todos" className={linkClass}>
                All todos
              </NavLink>
              <NavLink to="/admin/users" className={linkClass}>
                Users
              </NavLink>
            </RequireRole>
          </nav>
          <DropdownMenu>
            <DropdownMenuTrigger render={<Button variant="ghost" className="gap-2" />}>
              <Avatar className="size-6">
                <AvatarFallback className="text-[10px]">{initials}</AvatarFallback>
              </Avatar>
              <span className="hidden sm:inline">{user?.firstName}</span>
              <ChevronDownIcon />
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" className="w-56">
              <DropdownMenuGroup>
                <DropdownMenuLabel className="font-normal">
                  <div className="text-sm font-medium">
                    {user?.firstName} {user?.lastName}
                  </div>
                  <div className="truncate text-xs text-muted-foreground">{user?.email}</div>
                </DropdownMenuLabel>
              </DropdownMenuGroup>
              <DropdownMenuSeparator />
              <DropdownMenuItem onClick={() => navigate('/profile')}>Profile</DropdownMenuItem>
              <DropdownMenuItem onClick={onLogout}>
                <LogOutIcon /> Sign out
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </header>
      <main className="mx-auto max-w-5xl p-4 py-8">
        <Outlet />
      </main>
    </div>
  )
}
