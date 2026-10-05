import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthContext, type AuthContextValue } from '@/features/auth/auth-context'
import * as adminService from '@/services/admin.service'
import { todoService } from '@/services/todo.service'
import type { AdminTodo, ProcessingStatus } from '@/types/api'
import { AdminTodosPage } from './AdminTodosPage'

vi.mock('@/services/todo.service')
vi.mock('@/services/admin.service')

const todo = (id: string, title: string, status: ProcessingStatus, extra: Partial<AdminTodo> = {}): AdminTodo => ({
  id,
  userId: 'u1',
  title,
  description: null,
  isCompleted: false,
  createdAt: '2026-10-04T10:00:00Z',
  updatedAt: '2026-10-04T10:00:00Z',
  dueDate: null,
  processingStatus: status,
  queuedAt: '2026-10-04T10:00:01Z',
  workerId: 'worker-01',
  lastAttemptAt: '2026-10-04T10:00:02Z',
  processedAt: null,
  processingError: null,
  processingUpdatedAt: null,
  ...extra,
})

function renderAs(roles: string[]) {
  const value: AuthContextValue = {
    status: 'authenticated',
    user: { id: 'a', email: 'a@b.c', firstName: 'A', lastName: 'B', roles, permissions: [], enabled: true, createdAt: '', lastLoginAt: null },
    sessionExpired: false,
    error: null,
    login: vi.fn(),
    logout: vi.fn(),
    hasRole: (r) => roles.includes(r),
    clearSessionExpired: vi.fn(),
  }
  return render(
    <AuthContext.Provider value={value}>
      <MemoryRouter>
        <AdminTodosPage />
      </MemoryRouter>
    </AuthContext.Provider>,
  )
}

const rows = [
  todo('1', 'Waiting one', 'WAITING_RELEASE'),
  todo('2', 'Broken one', 'FAILED', { processingError: 'processing failed' }),
  todo('3', 'Done one', 'COMPLETED'),
]

describe('AdminTodosPage', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.mocked(adminService.listUsers).mockResolvedValue({
      content: [{ id: 'u1', email: 'jane@example.com' }],
      page: 0,
      size: 100,
      totalElements: 1,
      totalPages: 1,
    } as never)
    vi.mocked(todoService.adminList).mockResolvedValue({ content: rows, page: 0, size: 100, totalElements: 3, totalPages: 1 })
  })

  it('shows the processing metadata columns and status of every todo', async () => {
    renderAs(['USER', 'ADMIN'])
    expect(await screen.findByText('Waiting one')).toBeInTheDocument()
    for (const header of ['Todo', 'User', 'Processing status', 'Created', 'Queued', 'Worker', 'Last attempt', 'Actions']) {
      expect(screen.getByRole('columnheader', { name: header })).toBeInTheDocument()
    }
    expect(screen.getAllByText('jane@example.com').length).toBeGreaterThan(0) // owner resolved from the identity service
    const badges = Array.from(document.querySelectorAll('[data-status]')).map((b) => b.getAttribute('data-status'))
    expect(badges).toEqual(['WAITING_RELEASE', 'FAILED', 'COMPLETED'])
    expect(screen.getByText('processing failed')).toBeInTheDocument()
  })

  it('offers Release only for waiting todos and Retry only for failed ones (to admins)', async () => {
    renderAs(['USER', 'ADMIN'])
    await screen.findByText('Waiting one')
    expect(screen.getAllByRole('button', { name: /^release$/i })).toHaveLength(1)
    expect(screen.getAllByRole('button', { name: /^retry$/i })).toHaveLength(1)
  })

  it('calls the API to release one todo', async () => {
    vi.mocked(todoService.release).mockResolvedValue(undefined)
    renderAs(['USER', 'ADMIN'])
    await screen.findByText('Waiting one')

    await userEvent.click(screen.getByRole('button', { name: /^release$/i }))

    await waitFor(() => expect(todoService.release).toHaveBeenCalledWith('1'))
  })

  it('calls the API to retry a failed todo', async () => {
    vi.mocked(todoService.retry).mockResolvedValue(undefined)
    renderAs(['USER', 'ADMIN'])
    await screen.findByText('Broken one')

    await userEvent.click(screen.getByRole('button', { name: /^retry$/i }))

    await waitFor(() => expect(todoService.retry).toHaveBeenCalledWith('2'))
  })

  it('hides every operational action from a non-admin (the API would return 403 anyway)', async () => {
    renderAs(['USER'])
    await screen.findByText('Waiting one')
    expect(screen.queryByRole('button', { name: /^release$/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^retry$/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /release all/i })).not.toBeInTheDocument()
  })

  it('shows the API error when the server refuses (e.g. 403) instead of assuming success', async () => {
    vi.mocked(todoService.adminList).mockRejectedValue(Object.assign(new Error('forbidden'), { status: 403 }))
    renderAs(['USER', 'ADMIN'])
    // a non-ApiError is reported generically; the page never renders data it did not receive
    expect(await screen.findByText(/could not load todos/i)).toBeInTheDocument()
    expect(screen.queryByText('Waiting one')).not.toBeInTheDocument()
  })
})
