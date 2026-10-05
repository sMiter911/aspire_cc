import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/lib/api'
import { AuthContext, type AuthContextValue } from './auth-context'
import { LoginForm } from './LoginForm'

function renderForm(login: AuthContextValue['login'] = vi.fn().mockResolvedValue(undefined)) {
  const value: AuthContextValue = {
    status: 'unauthenticated',
    user: null,
    sessionExpired: false,
    error: null,
    login,
    logout: vi.fn(),
    hasRole: () => false,
    clearSessionExpired: vi.fn(),
  }
  render(
    <MemoryRouter>
      <AuthContext.Provider value={value}>
        <LoginForm />
      </AuthContext.Provider>
    </MemoryRouter>,
  )
  return login
}

describe('LoginForm', () => {
  it('shows required-field errors and does not call the API when empty', async () => {
    const login = renderForm()
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }))
    expect(await screen.findByText('Email is required')).toBeInTheDocument()
    expect(screen.getByText('Password is required')).toBeInTheDocument()
    expect(login).not.toHaveBeenCalled()
  })

  it('rejects a malformed email', async () => {
    const login = renderForm()
    await userEvent.type(screen.getByLabelText('Email'), 'not-an-email')
    await userEvent.type(screen.getByLabelText('Password'), 'whatever')
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }))
    expect(await screen.findByText('Enter a valid email address')).toBeInTheDocument()
    expect(login).not.toHaveBeenCalled()
  })

  it('submits trimmed credentials', async () => {
    const login = renderForm()
    await userEvent.type(screen.getByLabelText('Email'), '  jane@example.com ')
    await userEvent.type(screen.getByLabelText('Password'), 'secret')
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }))
    await waitFor(() => expect(login).toHaveBeenCalledWith({ email: 'jane@example.com', password: 'secret' }))
  })

  it('shows a generic message for invalid credentials', async () => {
    renderForm(vi.fn().mockRejectedValue(new ApiError(401, 'INVALID_CREDENTIALS', 'Invalid email or password')))
    await userEvent.type(screen.getByLabelText('Email'), 'jane@example.com')
    await userEvent.type(screen.getByLabelText('Password'), 'bad')
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }))
    expect(await screen.findByText('Invalid email or password.')).toBeInTheDocument()
  })

  it('tells the user when they are rate limited', async () => {
    renderForm(vi.fn().mockRejectedValue(new ApiError(429, 'RATE_LIMITED', 'Too many requests')))
    await userEvent.type(screen.getByLabelText('Email'), 'jane@example.com')
    await userEvent.type(screen.getByLabelText('Password'), 'bad')
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }))
    expect(await screen.findByText(/too many attempts/i)).toBeInTheDocument()
  })
})
