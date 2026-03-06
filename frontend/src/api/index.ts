import { Client } from './client'
import type { AppInfoResponse, DeviceInfoResponse, DeviceStatusResponse, AppUsageResponse, DailyReportResponse, WeeklyReportResponse } from './client'

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
    return await client.daily(
      params.deviceId,
      params.date ? new Date(params.date + 'T00:00:00') : undefined,
    )
  } catch {
    return null
  }
}

export async function fetchWeeklyReport(params: {
  deviceId?: number
  date?: string
}): Promise<WeeklyReportResponse | null> {
  try {
    return await client.weekly(
      params.deviceId,
      params.date ? new Date(params.date + 'T00:00:00') : undefined,
    )
  } catch {
    return null
  }
}

export function getIconUrl(appId: number): string {
  return `${API_BASE}/apps/${appId}/icon`
}
