import type { AppUsage } from './types'

const BASE = import.meta.env.DEV
  ? '/api/v1'
  : '/heartbeat/api/v1'

export async function fetchDevices(): Promise<string[]> {
  const res = await fetch(`${BASE}/devices`)
  if (!res.ok) return []
  return res.json()
}

export async function fetchUsage(deviceName?: string, date?: string): Promise<AppUsage[]> {
  const params = new URLSearchParams()
  if (deviceName) params.set('deviceName', deviceName)
  if (date) params.set('date', date)
  const res = await fetch(`${BASE}/usage?${params}`)
  if (!res.ok) return []
  return res.json()
}

export function getIconUrl(appName: string): string {
  return `${BASE}/icons/${encodeURIComponent(appName)}`
}
