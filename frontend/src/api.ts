import type { AppInfo, AppUsage, DailyReport, DeviceInfo, DeviceStatus, WeeklyReport } from './types'

const BASE = import.meta.env.DEV
  ? '/api/v1'
  : '/heartbeat/api/v1'

export async function fetchDevices(): Promise<DeviceInfo[]> {
  const res = await fetch(`${BASE}/devices`)
  if (!res.ok) return []
  return res.json()
}

export async function fetchApps(): Promise<AppInfo[]> {
  const res = await fetch(`${BASE}/apps`)
  if (!res.ok) return []
  return res.json()
}

export async function fetchDeviceStatus(deviceId: number): Promise<DeviceStatus | null> {
  const res = await fetch(`${BASE}/devices/${deviceId}`)
  if (!res.ok) return null
  return res.json()
}

export async function fetchUsage(params: {
  deviceId?: number
  start?: string
  end?: string
}): Promise<AppUsage[]> {
  const sp = new URLSearchParams()
  if (params.deviceId) sp.set('deviceId', params.deviceId.toString())
  if (params.start) sp.set('start', params.start)
  if (params.end) sp.set('end', params.end)
  const res = await fetch(`${BASE}/usage?${sp}`)
  if (!res.ok) return []
  return res.json()
}

export async function fetchDailyReport(params: {
  deviceId?: number
  date?: string
}): Promise<DailyReport | null> {
  const sp = new URLSearchParams()
  if (params.deviceId) sp.set('deviceId', params.deviceId.toString())
  if (params.date) sp.set('date', params.date)
  const res = await fetch(`${BASE}/reports/daily?${sp}`)
  if (!res.ok) return null
  return res.json()
}

export async function fetchWeeklyReport(params: {
  deviceId?: number
  date?: string
}): Promise<WeeklyReport | null> {
  const sp = new URLSearchParams()
  if (params.deviceId) sp.set('deviceId', params.deviceId.toString())
  if (params.date) sp.set('date', params.date)
  const res = await fetch(`${BASE}/reports/weekly?${sp}`)
  if (!res.ok) return null
  return res.json()
}

export function getIconUrl(appId: number): string {
  return `${BASE}/apps/${appId}/icon`
}
