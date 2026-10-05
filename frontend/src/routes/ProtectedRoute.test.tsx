import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { describe, expect, it } from 'vitest'
import { AuthContext, type AuthContextValue, type AuthStatus } from '@/features/auth/auth-context'
import type { User } from '@/types/api'
import { ProtectedRoute } from './ProtectedRoute'

function makeUser(roles: string[]): User {
  return {
    id: '1',
    email: 'a@b.c',
    firstName: 'A',
    lastName: 'B',
    roles,
    permissions: [],
    enabled: true,
    createdAt: '2026-01-01T00:00:00Z',
    lastLoginAt: null,
  }
}

function renderAt(path: string, status: AuthStatus, roles: string[] = [], sessionExpired = false) {
  const user = status === 'authenticated' ? makeUser(roles) : null
  const value: AuthContextValue = {
    status,
    user,
    sessionExpired,
    error: null,
    login: async () => {},
    logout: async () => {},
    hasRole: (r) => roles.includes(r) && status === 'authenticated',
    clearSessionExpired: () => {},
  }
  render(
    <AuthContext.Provider value={value}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/login" element={<div>login page</div>} />
          <Route path="/forbidden" element={<div>forbidden page</div>} />
          <Route element={<ProtectedRoute />}>
            <Route path="/dashboard" element={<div>dashboard</div>} />
          </Route>
          <Route element={<ProtectedRoute role="ADMIN" />}>
            <Route path="/admin" element={<div>admin area</div>} />
          </Route>
        </Routes>
      </MemoryRouter>
    </AuthContext.Provider>,
  )
}

describe('ProtectedRoute', () => {
  it('shows a spinner while the session is being restored', () => {
    renderAt('/dashboard', 'loading')
    expect(screen.getByRole('status', { name: 'Loading' })).toBeInTheDocument()
    expect(screen.queryByText('dashboard')).not.toBeInTheDocument()
    expect(screen.queryByText('login page')).not.toBeInTheDocument()
  })

  it('redirects unauthenticated visitors to /login', () => {
    renderAt('/dashboard', 'unauthenticated')
    expect(screen.getByText('login page')).toBeInTheDocument()
  })

  it('renders the page for an authenticated user', () => {
    renderAt('/dashboard', 'authenticated', ['USER'])
    expect(screen.getByText('dashboard')).toBeInTheDocument()
  })

  it('sends a USER away from ADMIN routes', () => {
    renderAt('/admin', 'authenticated', ['USER'])
    expect(screen.getByText('forbidden page')).toBeInTheDocument()
    expect(screen.queryByText('admin area')).not.toBeInTheDocument()
  })

  it('lets an ADMIN into ADMIN routes', () => {
    renderAt('/admin', 'authenticated', ['USER', 'ADMIN'])
    expect(screen.getByText('admin area')).toBeInTheDocument()
  })

  it('still redirects to /login when the session expired', () => {
    renderAt('/dashboard', 'unauthenticated', [], true)
    expect(screen.getByText('login page')).toBeInTheDocument()
  })
})
