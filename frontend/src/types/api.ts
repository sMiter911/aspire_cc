export interface User {
  id: string
  email: string
  firstName: string
  lastName: string
  roles: string[]
  permissions: string[]
  enabled: boolean
  createdAt: string
  lastLoginAt: string | null
}

export interface AuthResponse {
  accessToken: string
  tokenType: 'Bearer'
  expiresIn: number
  user: User
}

export interface RegisterPayload {
  email: string
  password: string
  firstName: string
  lastName: string
}

export interface LoginPayload {
  email: string
  password: string
}

export interface Page<T> {
  content: T[]
  page: number
  size: number
  totalElements: number
  totalPages: number
}

export interface FieldError {
  field: string
  message: string
}

/** Error envelope returned by every failing API call. */
export interface ApiErrorBody {
  timestamp: string
  status: number
  code: string
  message: string
  path: string
  errors: FieldError[]
}

/** A todo as returned by the Todo API. `userId` is the owner's id from the identity service. */
export type ProcessingStatus = 'QUEUED' | 'PROCESSING' | 'WAITING_RELEASE' | 'PERSISTING' | 'COMPLETED' | 'FAILED'

export interface Todo {
  id: string
  userId: string
  title: string
  description: string | null
  isCompleted: boolean
  createdAt: string
  updatedAt: string
  dueDate: string | null
  /** Asynchronous processing state, reported by the Go worker and persisted by the Todo API. */
  processingStatus: ProcessingStatus
  processedAt: string | null
}

/** What /api/admin/todos returns: the todo plus worker/attempt metadata that only admins receive. */
export interface AdminTodo extends Omit<Todo, 'processedAt'> {
  queuedAt: string | null
  workerId: string | null
  lastAttemptAt: string | null
  processedAt: string | null
  processingError: string | null
  processingUpdatedAt: string | null
}

export interface TodoInput {
  title: string
  description: string | null
  dueDate: string | null
  isCompleted: boolean
}
