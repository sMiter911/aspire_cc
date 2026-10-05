import { render, screen } from '@testing-library/react'
import type { ReactNode } from 'react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { RequirePermission, RequireRole } from '@/components/RequireAuthorization'
import { AuthContext, type AuthContextValue } from '@/features/auth/auth-context'
import { ProtectedRoute } from '@/routes/ProtectedRoute'
import { capabilitiesFor } from './authorization'

function withRoles(roles: string[] | null, children: ReactNode) {
  const value: AuthContextValue = {
    status: roles ? 'authenticated' : 'unauthenticated',
    user: roles
      ? { id: '1', email: 'a@b.c', firstName: 'A', lastName: 'B', roles, permissions: [], enabled: true, createdAt: '', lastLoginAt: null }
      : null,
    sessionExpired: false,
    error: null,
    login: vi.fn(),
    logout: vi.fn(),
    hasRole: (r) => roles?.includes(r) ?? false,
    clearSessionExpired: vi.fn(),
  }
  return render(
    <AuthContext.Provider value={value}>
      <MemoryRouter initialEntries={['/admin/todos']}>{children}</MemoryRouter>
    </AuthContext.Provider>,
  )
}

describe('capabilities', () => {
  it('USER can only manage own todos', () => {
    const caps = capabilitiesFor(['USER'])
    expect(caps.has('todos:manage-own')).toBe(true)
    expect(caps.has('todos:read-all')).toBe(false)
    expect(caps.has('todos:manage-all')).toBe(false)
  })

  it('ADMIN can manage all todos', () => {
    const caps = capabilitiesFor(['USER', 'ADMIN'])
    expect(caps.has('todos:read-all')).toBe(true)
    expect(caps.has('todos:manage-all')).toBe(true)
  })

  it('ignores roles it does not know (e.g. a tampered or future role)', () => {
    expect(capabilitiesFor(['SUPERUSER']).size).toBe(0)
  })
})

describe('<RequireRole> / <RequirePermission>', () => {
  it('hides admin UI from a USER', () => {
    withRoles(
      ['USER'],
      <>
        <RequireRole role="ADMIN">
          <button>Admin todos</button>
        </RequireRole>
        <RequirePermission permission="todos:manage-own">
          <button>New todo</button>
        </RequirePermission>
      </>,
    )
    expect(screen.queryByText('Admin todos')).not.toBeInTheDocument()
    expect(screen.getByText('New todo')).toBeInTheDocument()
  })

  it('shows admin UI to an ADMIN and a fallback to others', () => {
    withRoles(
      ['USER', 'ADMIN'],
      <RequirePermission permission="todos:read-all" fallback={<span>no access</span>}>
        <button>All todos</button>
      </RequirePermission>,
    )
    expect(screen.getByText('All todos')).toBeInTheDocument()
    expect(screen.queryByText('no access')).not.toBeInTheDocument()
  })

  it('renders the fallback when signed out', () => {
    withRoles(
      null,
      <RequireRole role="USER" fallback={<span>sign in</span>}>
        <button>secret</button>
      </RequireRole>,
    )
    expect(screen.getByText('sign in')).toBeInTheDocument()
  })
})

describe('<ProtectedRoute permission>', () => {
  const tree = (
    <Routes>
      <Route path="/forbidden" element={<div>forbidden page</div>} />
      <Route path="/login" element={<div>login page</div>} />
      <Route element={<ProtectedRoute permission="todos:read-all" />}>
        <Route path="/admin/todos" element={<div>admin todos page</div>} />
      </Route>
    </Routes>
  )

  it('sends a USER who types /admin/todos to /forbidden', () => {
    withRoles(['USER'], tree)
    expect(screen.getByText('forbidden page')).toBeInTheDocument()
  })

  it('lets an ADMIN in', () => {
    withRoles(['USER', 'ADMIN'], tree)
    expect(screen.getByText('admin todos page')).toBeInTheDocument()
  })

  it('sends anonymous visitors to /login', () => {
    withRoles(null, tree)
    expect(screen.getByText('login page')).toBeInTheDocument()
  })
})
