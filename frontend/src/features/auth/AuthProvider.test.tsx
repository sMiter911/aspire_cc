import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/lib/api'
import * as authService from '@/services/auth.service'
import type { AuthResponse, User } from '@/types/api'
import { useAuth } from '@/hooks/useAuth'
import { AuthProvider } from './AuthProvider'

vi.mock('@/services/auth.service')

const user: User = {
  id: '1',
  email: 'jane@example.com',
  firstName: 'Jane',
  lastName: 'Doe',
  roles: ['USER'],
  permissions: ['PROFILE_READ'],
  enabled: true,
  createdAt: '2026-01-01T00:00:00Z',
  lastLoginAt: null,
}
const auth: AuthResponse = { accessToken: 'a.b.c', tokenType: 'Bearer', expiresIn: 600, user }

function Probe() {
  const { status, user, sessionExpired, error, login, logout, hasRole } = useAuth()
  return (
    <div>
      <span data-testid="status">{status}</span>
      <span data-testid="email">{user?.email ?? '-'}</span>
      <span data-testid="expired">{String(sessionExpired)}</span>
      <span data-testid="error">{error ?? '-'}</span>
      <span data-testid="admin">{String(hasRole('ADMIN'))}</span>
      <button onClick={() => login({ email: 'jane@example.com', password: 'pw' }).catch(() => {})}>login</button>
      <button onClick={() => logout()}>logout</button>
    </div>
  )
}

const renderProvider = () =>
  render(
    <AuthProvider>
      <Probe />
    </AuthProvider>,
  )

describe('AuthProvider', () => {
  beforeEach(() => {
    vi.resetAllMocks()
  })

  it('starts in loading and becomes authenticated when the session can be restored', async () => {
    vi.mocked(authService.restoreSession).mockResolvedValue(auth)
    renderProvider()
    expect(screen.getByTestId('status')).toHaveTextContent('loading')
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('authenticated'))
    expect(screen.getByTestId('email')).toHaveTextContent('jane@example.com')
  })

  it('treats a 401 on restore as "no session" without an error or expiry flag', async () => {
    vi.mocked(authService.restoreSession).mockRejectedValue(new ApiError(401, 'INVALID_REFRESH_TOKEN', 'x'))
    renderProvider()
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated'))
    expect(screen.getByTestId('error')).toHaveTextContent('-')
    expect(screen.getByTestId('expired')).toHaveTextContent('false')
  })

  it('surfaces a connection problem when the API is unreachable', async () => {
    vi.mocked(authService.restoreSession).mockRejectedValue(new ApiError(0, 'NETWORK_ERROR', 'Unable to reach the server'))
    renderProvider()
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated'))
    expect(screen.getByTestId('error')).toHaveTextContent('Unable to reach the server')
  })

  it('login authenticates, logout clears the session', async () => {
    vi.mocked(authService.restoreSession).mockRejectedValue(new ApiError(0, 'NETWORK_ERROR', 'down'))
    vi.mocked(authService.login).mockResolvedValue(auth)
    vi.mocked(authService.logout).mockResolvedValue()
    renderProvider()
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated'))

    await userEvent.click(screen.getByText('login'))
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('authenticated'))
    expect(screen.getByTestId('email')).toHaveTextContent('jane@example.com')
    expect(screen.getByTestId('admin')).toHaveTextContent('false')

    await act(async () => {
      await userEvent.click(screen.getByText('logout'))
    })
    expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    expect(screen.getByTestId('email')).toHaveTextContent('-')
    expect(authService.logout).toHaveBeenCalledOnce()
  })

  it('flags an expired session when a refresh fails after being signed in', async () => {
    vi.mocked(authService.restoreSession).mockRejectedValue(new ApiError(0, 'NETWORK_ERROR', 'down'))
    vi.mocked(authService.login).mockResolvedValue(auth)
    renderProvider()
    await userEvent.click(await screen.findByText('login'))
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('authenticated'))

    // The api layer announces a failed refresh through the registered listener.
    const api = await import('@/lib/api')
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ code: 'INVALID_REFRESH_TOKEN', message: 'expired' }), {
        status: 401,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
    await act(async () => {
      await api.refreshSession().catch(() => {})
    })

    expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated')
    expect(screen.getByTestId('expired')).toHaveTextContent('true')
  })
})
