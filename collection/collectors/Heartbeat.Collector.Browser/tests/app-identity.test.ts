import { describe, expect, it } from 'vitest'
import { detectBrowserAppIdentity } from '../src/app-identity'

describe('Browser-owned stable App identity', () => {
  it.each([
    ['Google Chrome', 'win', 'win:chrome'],
    ['Microsoft Edge', 'win', 'win:msedge'],
    ['Google Chrome', 'mac', 'mac:com.google.chrome'],
    ['Microsoft Edge', 'mac', 'mac:com.microsoft.edgemac'],
  ])('identifies %s on %s without a Host resolver', (brand, platform, expected) => {
    expect(detectBrowserAppIdentity({ brands: ['Not_A Brand', 'Chromium', brand], platform })).toBe(expected)
  })

  it.each([
    { brands: ['Chromium'], platform: 'win', userAgent: 'Chrome/130 Safari/537.36' },
    { brands: ['Google Chrome', 'Microsoft Edge'], platform: 'win' },
    { brands: ['Google Chrome', 'Acme Browser'], platform: 'win' },
    { brands: ['Google Chrome'], platform: 'linux' },
    { brands: ['Google Chrome'], platform: 'win', hasBraveApi: true },
    { brands: ['Brave'], platform: 'mac' },
    { brands: ['Google Chrome'], platform: 'mac', userAgent: 'Edg/130' },
  ])('does not guess unsupported or conflicting identity %j', signals => {
    expect(detectBrowserAppIdentity(signals)).toBeUndefined()
  })
})
