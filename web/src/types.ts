export interface AppUsage {
  id: number
  deviceName: string
  appName: string
  startTime: string
  endTime: string
  durationSeconds: number
}

export interface AppSummary {
  appName: string
  totalSeconds: number
}
