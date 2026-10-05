import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { setAccessToken } from '@/lib/api'
import { todoService } from './todo.service'

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

describe('todoService', () => {
  beforeEach(() => setAccessToken('tok'))
  afterEach(() => vi.restoreAllMocks())

  it('never sends an owner when creating a todo', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async () => json(201, { id: '1' }))
    await todoService.create({ title: 'x', description: null, dueDate: null })
    const body = JSON.parse(String(fetchMock.mock.calls[0][1]!.body))
    expect(body).toEqual({ title: 'x', description: null, dueDate: null })
    expect(body).not.toHaveProperty('userId')
  })

  it('targets the todo API paths with the bearer token', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async () => json(200, { content: [] }))
    await todoService.adminList({ userId: 'u1', completed: false })
    const [url, init] = fetchMock.mock.calls[0]
    expect(String(url)).toBe('/api/admin/todos?userId=u1&completed=false')
    expect((init!.headers as Record<string, string>).Authorization).toBe('Bearer tok')
  })

  it('surfaces a 403 from the API so the UI can show it (the UI never assumes access)', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async () => json(403, { code: 'ACCESS_DENIED', message: 'no' }))
    await expect(todoService.adminList()).rejects.toMatchObject({ status: 403, code: 'ACCESS_DENIED' })
  })
})
