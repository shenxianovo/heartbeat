import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import type { AppInfoResponse, AppUsageResponse, AppSummary, DeviceInfoResponse, DeviceStatusResponse, DailyReportResponse, WeeklyReportResponse } from '../api/index'
import { fetchDevices, fetchApps, fetchUsage, fetchDeviceStatus, fetchDailyReport, fetchWeeklyReport } from '../api/index'

function todayStr(): string {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

export function formatDuration(sec: number): string {
  const h = Math.floor(sec / 3600)
  const m = Math.floor((sec % 3600) / 60)
  if (h > 0) return `${h}h ${m}m`
  if (m > 0) return `${m}m`
  return '< 1m'
}

export function useHeartbeat() {
  const devices = ref<DeviceInfoResponse[]>([])
  const apps = ref<AppInfoResponse[]>([])
  const selectedDevice = ref(0)
  const selectedDate = ref(todayStr())
  const usageData = ref<AppUsageResponse[]>([])
  const deviceStatus = ref<DeviceStatusResponse | null>(null)
  const dailyReport = ref<DailyReportResponse | null>(null)
  const weeklyReport = ref<WeeklyReportResponse | null>(null)
  const loading = ref(false)

  const appNameMap = computed(() => {
    const map = new Map<number, string>()
    for (const app of apps.value) map.set(app.id!, app.name!)
    return map
  })

  const selectedDeviceName = computed(() => {
    const d = devices.value.find(d => d.id === selectedDevice.value)
    return d?.name ?? ''
  })

  const isToday = computed(() => selectedDate.value === todayStr())
  const isAlive = computed(() => isToday.value && (deviceStatus.value?.isOnline ?? false))
  const currentApp = computed(() => deviceStatus.value?.currentApp ?? null)

  const currentAppId = computed(() => {
    const name = currentApp.value
    if (!name) return null
    for (const [id, n] of appNameMap.value) {
      if (n === name) return id
    }
    return null
  })

  const lastSeenStr = computed(() => {
    const raw = deviceStatus.value?.lastSeen
    if (!raw) return ''
    return raw.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })
  })

  const appSummaries = computed<AppSummary[]>(() => {
    if (!dailyReport.value?.apps) return []
    return dailyReport.value.apps
      .map(a => ({
        appId: a.appId!,
        appName: appNameMap.value.get(a.appId!) ?? `App ${a.appId}`,
        totalSeconds: a.durationSeconds!,
      }))
      .sort((a, b) => b.totalSeconds - a.totalSeconds)
  })

  const totalSeconds = computed(() => dailyReport.value?.totalSeconds ?? 0)
  const maxSeconds = computed(() => appSummaries.value[0]?.totalSeconds ?? 1)

  const activeHours = computed(() => {
    const hours = new Set<number>()
    for (const u of usageData.value) {
      const s = u.startTime!.getHours()
      const e = u.endTime!.getHours()
      if (e >= s) {
        for (let h = s; h <= e; h++) hours.add(h)
      } else {
        for (let h = s; h < 24; h++) hours.add(h)
      }
    }
    return hours
  })

  const weeklyAppSummaries = computed<AppSummary[]>(() => {
    if (!weeklyReport.value?.apps) return []
    return weeklyReport.value.apps
      .map(a => ({
        appId: a.appId!,
        appName: appNameMap.value.get(a.appId!) ?? `App ${a.appId}`,
        totalSeconds: a.durationSeconds!,
      }))
      .sort((a, b) => b.totalSeconds - a.totalSeconds)
  })

  const weeklyTotalSeconds = computed(() => weeklyReport.value?.totalSeconds ?? 0)

  async function loadUsage() {
    if (!selectedDevice.value) return
    const dateObj = new Date(selectedDate.value + 'T00:00:00')
    const start = dateObj.toISOString()
    const end = new Date(dateObj.getTime() + 86400000).toISOString()
    usageData.value = await fetchUsage({ deviceId: selectedDevice.value, start, end })
  }

  async function loadStatus() {
    if (!selectedDevice.value) return
    deviceStatus.value = await fetchDeviceStatus(selectedDevice.value)
  }

  async function loadDailyReport() {
    if (!selectedDevice.value) return
    dailyReport.value = await fetchDailyReport({ deviceId: selectedDevice.value, date: selectedDate.value })
  }

  async function loadWeeklyReport() {
    if (!selectedDevice.value) return
    weeklyReport.value = await fetchWeeklyReport({ deviceId: selectedDevice.value, date: selectedDate.value })
  }

  async function refresh() {
    loading.value = true
    try {
      await Promise.all([loadUsage(), loadStatus(), loadDailyReport(), loadWeeklyReport()])
    } finally {
      loading.value = false
    }
  }

  let statusTimer: ReturnType<typeof setInterval>
  let usageTimer: ReturnType<typeof setInterval>

  onMounted(async () => {
    const [deviceList, appList] = await Promise.all([fetchDevices(), fetchApps()])
    devices.value = deviceList
    apps.value = appList

    if (devices.value.length > 0) {
      let picked = devices.value[0].id!
      for (const d of devices.value) {
        const s = await fetchDeviceStatus(d.id!)
        if (s?.isOnline) { picked = d.id!; break }
      }
      selectedDevice.value = picked
    }

    statusTimer = setInterval(() => {
      if (isToday.value) loadStatus()
    }, 5_000)

    usageTimer = setInterval(() => {
      if (isToday.value) {
        loadUsage()
        loadDailyReport()
        loadWeeklyReport()
      }
    }, 30_000)
  })

  onUnmounted(() => {
    clearInterval(statusTimer)
    clearInterval(usageTimer)
  })

  watch([selectedDevice, selectedDate], () => refresh())

  return {
    devices,
    selectedDevice,
    selectedDeviceName,
    selectedDate,
    loading,
    isToday,
    isAlive,
    currentApp,
    currentAppId,
    lastSeenStr,
    appSummaries,
    totalSeconds,
    maxSeconds,
    activeHours,
    weeklyAppSummaries,
    weeklyTotalSeconds,
  }
}
