// @vitest-environment happy-dom

import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  fetchPublicKeyFrequency,
  fetchPublicInputCounts,
  fetchPublicSegments,
  fetchPublicUsage,
} from './index'

vi.mock('../stores/auth', () => ({
  authStore: {
    token: { value: null },
    tryRefresh: vi.fn(),
    clearAuth: vi.fn(),
  },
}))

const fetchMock = vi.fn()

describe('generic Instant Window transports', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.stubGlobal('fetch', fetchMock)
    fetchMock.mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      text: async () => '[]',
    } as Response)
  })

  it('keeps Usage, Segments, and Key Frequency independently callable with arbitrary instants', async () => {
    const start = '2026-04-12T03:17:00Z'
    const end = '2026-04-12T04:47:00Z'

    await fetchPublicUsage('alice', { deviceId: 0, start, end })
    await fetchPublicSegments('alice', { deviceId: 7, source: 'browser', appId: 9, start, end })
    fetchMock.mockResolvedValueOnce({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      text: async () => JSON.stringify({ keys: [] }),
    } as Response)
    await fetchPublicKeyFrequency('alice', { deviceId: 7, start, end })

    await fetchPublicInputCounts('alice', { deviceId: 0, start, end })
    expectRequest(3, '/api/v1/users/alice/input-events/counts', { start, end })

    expectRequest(0, '/api/v1/users/alice/usage', { start, end })
    expectRequest(1, '/api/v1/users/alice/segments', {
      deviceId: '7',
      source: 'browser',
      appId: '9',
      start,
      end,
    })
    expectRequest(2, '/api/v1/users/alice/input-events/key-frequency', {
      deviceId: '7',
      start,
      end,
    })
  })
})

function expectRequest(index: number, path: string, params: Record<string, string>) {
  const [rawUrl] = fetchMock.mock.calls[index]
  const url = new URL(rawUrl, 'https://heartbeat.test')
  expect(url.pathname).toBe(path)
  expect(Object.fromEntries(url.searchParams)).toEqual({
    ...params,
    start: new Date(params.start).toISOString(),
    end: new Date(params.end).toISOString(),
  })
  expect(url.searchParams.has('Version')).toBe(false)
  expect(url.searchParams.has('LocalDate')).toBe(false)
  expect(url.searchParams.has('TimeZone')).toBe(false)
}
