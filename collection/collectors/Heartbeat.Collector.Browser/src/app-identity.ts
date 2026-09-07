export interface BrowserIdentitySignals {
  brands?: readonly string[]
  userAgent?: string
  hasBraveApi?: boolean
  /** chrome.runtime.getPlatformInfo().os, independent of UA platform emulation. */
  platform: string
}

/** Only verified Chrome/Edge brands on Windows/macOS have a supported stable App identity. */
export function detectBrowserAppIdentity(signals: BrowserIdentitySignals): string | undefined {
  if (signals.hasBraveApi) return undefined
  const candidates = new Set<'chrome' | 'edge'>()
  for (const value of signals.brands ?? []) {
    const brand = value.trim().toLowerCase()
    if (brand === 'google chrome') candidates.add('chrome')
    else if (brand === 'microsoft edge') candidates.add('edge')
    else if (brand !== 'chromium' && brand.replace(/[^a-z0-9]/g, '') !== 'notabrand') return undefined
  }
  const ua = signals.userAgent ?? ''
  if (/\b(?:OPR|Vivaldi|Firefox|EdgA|EdgiOS)\//i.test(ua)) return undefined
  if (/\bEdg\//.test(ua)) candidates.add('edge')
  if (candidates.size !== 1) return undefined
  const browser = [...candidates][0]
  if (signals.platform === 'win') return browser === 'chrome' ? 'win:chrome' : 'win:msedge'
  if (signals.platform === 'mac') return browser === 'chrome' ? 'mac:com.google.chrome' : 'mac:com.microsoft.edgemac'
  return undefined
}
