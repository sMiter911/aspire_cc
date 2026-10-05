import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, apiRequest, getAccessToken, onSessionChange, setAccessToken } from './api'

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const authBody = { accessToken: 'new-token', tokenType: 'Bearer', expiresIn: 600, user: { id: '1' } }

describe('api client', () => {
  beforeEach(() => {
    setAccessToken('old-token')
    onSessionChange(null)
  })
  afterEach(() => vi.restoreAllMocks())

  it('sends the bearer token and always includes credentials', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(200, { ok: true }))
    await apiRequest('/api/users/me')
    const [, init] = fetchMock.mock.calls[0]
    expect((init!.headers as Record<string, string>).Authorization).toBe('Bearer old-token')
    expect(init!.credentials).toBe('include')
  })

  it('never writes tokens to web storage', async () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem')
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(200, {}))
    await apiRequest('/api/users/me')
    expect(setItem).not.toHaveBeenCalled()
  })

  it('refreshes once on 401 and replays the request with the new token', async () => {
    const fetchMock = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(json(401, { code: 'UNAUTHENTICATED', message: 'x' }))
      .mockResolvedValueOnce(json(200, authBody)) // /api/auth/refresh
      .mockResolvedValueOnce(json(200, { id: '1' })) // replay

    const result = await apiRequest<{ id: string }>('/api/users/me')

    expect(result).toEqual({ id: '1' })
    expect(fetchMock.mock.calls[1][0]).toContain('/api/auth/refresh')
    const refreshHeaders = fetchMock.mock.calls[1][1]!.headers as Record<string, string>
    expect(refreshHeaders['X-Requested-With']).toBe('XMLHttpRequest') // CSRF header
    expect(refreshHeaders.Authorization).toBeUndefined()
    const replayHeaders = fetchMock.mock.calls[2][1]!.headers as Record<string, string>
    expect(replayHeaders.Authorization).toBe('Bearer new-token')
    expect(getAccessToken()).toBe('new-token')
  })

  it('shares a single refresh between concurrent 401s (tokens are one-time use)', async () => {
    let refreshCalls = 0
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
      const url = String(input)
      if (url.includes('/api/auth/refresh')) {
        refreshCalls++
        await new Promise((r) => setTimeout(r, 10))
        return json(200, authBody)
      }
      const auth = (init!.headers as Record<string, string>).Authorization
      return auth === 'Bearer new-token' ? json(200, { ok: true }) : json(401, { code: 'UNAUTHENTICATED' })
    })

    await Promise.all([apiRequest('/api/a'), apiRequest('/api/b'), apiRequest('/api/c')])

    expect(refreshCalls).toBe(1)
  })

  it('reports the session as ended when refresh itself is rejected', async () => {
    const listener = vi.fn()
    onSessionChange(listener)
    vi.spyOn(globalThis, 'fetch').mockImplementation(async () => json(401, { code: 'INVALID_REFRESH_TOKEN', message: 'x' }))

    await expect(apiRequest('/api/users/me')).rejects.toBeInstanceOf(ApiError)

    expect(listener).toHaveBeenCalledWith(null)
    expect(getAccessToken()).toBeNull()
  })

  it('does not try to refresh on authorization failures (403)', async () => {
    const fetchMock = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValue(json(403, { code: 'ACCESS_DENIED', message: 'nope' }))
    await expect(apiRequest('/api/admin/users')).rejects.toMatchObject({ status: 403, code: 'ACCESS_DENIED' })
    expect(fetchMock).toHaveBeenCalledOnce()
  })

  it('maps network failures to a friendly ApiError', async () => {
    vi.spyOn(globalThis, 'fetch').mockRejectedValue(new TypeError('Failed to fetch'))
    await expect(apiRequest('/api/users/me')).rejects.toMatchObject({ status: 0, code: 'NETWORK_ERROR' })
  })
})
