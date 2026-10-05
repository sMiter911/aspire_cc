import { apiRequest } from '@/lib/api'
import type { AdminTodo, Page, Todo, TodoInput } from '@/types/api'

/** Calls the ASP.NET Core Todo API (same origin in dev: Vite proxies /api/todos to it). */
export interface TodoQuery {
  completed?: boolean
  page?: number
  size?: number
}

function qs(params: Record<string, string | number | boolean | undefined>) {
  const sp = new URLSearchParams()
  Object.entries(params).forEach(([k, v]) => v !== undefined && v !== '' && sp.set(k, String(v)))
  const s = sp.toString()
  return s ? `?${s}` : ''
}

export const todoService = {
  list: (q: TodoQuery = {}) => apiRequest<Page<Todo>>(`/api/todos${qs({ ...q })}`),
  get: (id: string) => apiRequest<Todo>(`/api/todos/${id}`),
  // The owner is never sent: the API derives it from the access token.
  create: (input: Omit<TodoInput, 'isCompleted'>) => apiRequest<Todo>('/api/todos', { method: 'POST', body: input }),
  update: (id: string, input: TodoInput) => apiRequest<Todo>(`/api/todos/${id}`, { method: 'PUT', body: input }),
  complete: (id: string) => apiRequest<Todo>(`/api/todos/${id}/complete`, { method: 'PATCH' }),
  remove: (id: string) => apiRequest<void>(`/api/todos/${id}`, { method: 'DELETE' }),
  adminList: (q: TodoQuery & { userId?: string } = {}) => apiRequest<Page<AdminTodo>>(`/api/admin/todos${qs({ ...q })}`),
  // Operational actions. The UI only offers them to admins; the Todo API authorizes every call (403 otherwise)
  // and the Go worker never sees a user: it just receives the command the API chose to publish.
  release: (id: string) => apiRequest<void>(`/api/admin/todos/${id}/release`, { method: 'POST' }),
  releaseAll: () => apiRequest<{ waiting: number }>('/api/admin/todos/release', { method: 'POST' }),
  retry: (id: string) => apiRequest<void>(`/api/admin/todos/${id}/retry`, { method: 'POST' }),
}
