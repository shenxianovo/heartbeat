import { Client, DailyReportResponse, WeeklyReportResponse } from './client'
import type { AppInfoResponse, DeviceInfoResponse, DeviceStatusResponse, AppUsageResponse } from './client'

// ===== Base URL =====
const BASE_URL = import.meta.env.DEV ? '' : '/heartbeat'
const API_BASE = `${BASE_URL}/api/v1`

const client = new Client(BASE_URL)

// Re-export generated types
export type { AppInfoResponse, DeviceInfoResponse, DeviceStatusResponse, AppUsageResponse, DailyReportResponse, WeeklyReportResponse }
export type { AppDurationItem } from './client'

export interface AppSummary {
  appId: number
  appName: string
  totalSeconds: number
}

/** 将 "yyyy-MM-dd" 格式化为带本地时区偏移的 ISO 字符串，如 "2026-03-06T00:00:00+08:00" */
function toLocalDateTimeOffsetString(dateStr: string): string {
  const offset = new Date().getTimezoneOffset()
  const sign = offset <= 0 ? '+' : '-'
  const absMin = Math.abs(offset)
  const h = String(Math.floor(absMin / 60)).padStart(2, '0')
  const m = String(absMin % 60).padStart(2, '0')
  return `${dateStr}T00:00:00${sign}${h}:${m}`
}

/** 获取浏览器时区标签，如 "UTC+8" */
export function getTimezoneLabel(): string {
  const offset = new Date().getTimezoneOffset()
  const sign = offset <= 0 ? '+' : '-'
  const absMin = Math.abs(offset)
  const h = Math.floor(absMin / 60)
  const m = absMin % 60
  return `UTC${sign}${h}${m > 0 ? ':' + String(m).padStart(2, '0') : ''}`
}

// ===== API Functions =====

export async function fetchDevices(): Promise<DeviceInfoResponse[]> {
  try {
    return await client.devicesAll()
  } catch {
    return []
  }
}

export async function fetchApps(): Promise<AppInfoResponse[]> {
  try {
    return await client.apps()
  } catch {
    return []
  }
}

export async function fetchDeviceStatus(deviceId: number): Promise<DeviceStatusResponse | null> {
  try {
    return await client.devices(deviceId)
  } catch {
    return null
  }
}

export async function fetchUsage(params: {
  deviceId?: number
  start?: string
  end?: string
}): Promise<AppUsageResponse[]> {
  try {
    return await client.usageAll(
      params.deviceId,
      params.start ? new Date(params.start) : undefined,
      params.end ? new Date(params.end) : undefined,
    )
  } catch {
    return []
  }
}

export async function fetchDailyReport(params: {
  deviceId?: number
  date?: string
}): Promise<DailyReportResponse | null> {
  try {
    const searchParams = new URLSearchParams()
    if (params.deviceId !== undefined) searchParams.set('deviceId', String(params.deviceId))
    if (params.date) searchParams.set('date', toLocalDateTimeOffsetString(params.date))
    const res = await fetch(`${API_BASE}/reports/daily?${searchParams}`)
    if (!res.ok) return null
    return DailyReportResponse.fromJS(await res.json())
  } catch {
    return null
  }
}

export async function fetchWeeklyReport(params: {
  deviceId?: number
  date?: string
}): Promise<WeeklyReportResponse | null> {
  try {
    const searchParams = new URLSearchParams()
    if (params.deviceId !== undefined) searchParams.set('deviceId', String(params.deviceId))
    if (params.date) searchParams.set('date', toLocalDateTimeOffsetString(params.date))
    const res = await fetch(`${API_BASE}/reports/weekly?${searchParams}`)
    if (!res.ok) return null
    return WeeklyReportResponse.fromJS(await res.json())
  } catch {
    return null
  }
}

export function getIconUrl(appId: number): string {
  return `${API_BASE}/apps/${appId}/icon`
}
