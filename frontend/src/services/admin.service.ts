import { apiRequest } from '@/lib/api'
import type { Page, User } from '@/types/api'

export function listUsers(page = 0, size = 20): Promise<Page<User>> {
  return apiRequest<Page<User>>(`/api/admin/users?page=${page}&size=${size}`)
}
