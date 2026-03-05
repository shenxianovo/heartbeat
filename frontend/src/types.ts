export interface DeviceInfo {
  id: number
  name: string
}

export interface AppInfo {
  id: number
  name: string
}

export interface AppUsage {
  id: number
  appId: number
  appName: string
  startTime: string
  endTime: string
  durationSeconds: number
}

export interface AppSummary {
  appId: number
  appName: string
  totalSeconds: number
}

export interface DeviceStatus {
  id: number
  currentApp: string | null
  lastSeen: string | null
  isOnline: boolean
}

export interface AppDurationItem {
  appId: number
  durationSeconds: number
}

export interface DailyReport {
  date: string
  totalSeconds: number
  apps: AppDurationItem[]
}

export interface WeeklyReport {
  weekStart: string
  weekEnd: string
  totalSeconds: number
  apps: AppDurationItem[]
}
