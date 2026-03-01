import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import type { AppUsage, AppSummary, DeviceStatus } from '../types'
import { fetchDevices, fetchUsage, fetchDeviceStatus } from '../api'

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
  // --- 状态 ---
  const devices = ref<string[]>([])
  const selectedDevice = ref('')
  const selectedDate = ref(todayStr())
  const usageData = ref<AppUsage[]>([])
  const deviceStatus = ref<DeviceStatus | null>(null)
  const loading = ref(false)

  // --- 计算属性 ---
  const isToday = computed(() => selectedDate.value === todayStr())

  const isAlive = computed(() => isToday.value && (deviceStatus.value?.isOnline ?? false))

  const currentApp = computed(() => deviceStatus.value?.currentApp ?? null)

  const lastSeenStr = computed(() => {
    const raw = deviceStatus.value?.lastSeen
    if (!raw) return ''
    return new Date(raw).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })
  })

  const appSummaries = computed<AppSummary[]>(() => {
    const map = new Map<string, number>()
    for (const u of usageData.value) {
      map.set(u.appName, (map.get(u.appName) ?? 0) + u.durationSeconds)
    }
    return [...map.entries()]
      .map(([appName, totalSeconds]) => ({ appName, totalSeconds }))
      .sort((a, b) => b.totalSeconds - a.totalSeconds)
  })

  const totalSeconds = computed(() =>
    appSummaries.value.reduce((s, a) => s + a.totalSeconds, 0)
  )

  const maxSeconds = computed(() => appSummaries.value[0]?.totalSeconds ?? 1)

  const activeHours = computed(() => {
    const hours = new Set<number>()
    for (const u of usageData.value) {
      const s = new Date(u.startTime).getHours()
      const e = new Date(u.endTime).getHours()
      if (e >= s) {
        for (let h = s; h <= e; h++) hours.add(h)
      } else {
        for (let h = s; h < 24; h++) hours.add(h)
      }
    }
    return hours
  })

  // --- 数据加载 ---
  async function loadUsage() {
    if (!selectedDevice.value) return
    loading.value = true
    try {
      usageData.value = await fetchUsage(selectedDevice.value, selectedDate.value)
    } finally {
      loading.value = false
    }
  }

  async function loadStatus() {
    if (!selectedDevice.value) return
    deviceStatus.value = await fetchDeviceStatus(selectedDevice.value)
  }

  async function refresh() {
    await Promise.all([loadUsage(), loadStatus()])
  }

  // --- 生命周期 ---
  let statusTimer: ReturnType<typeof setInterval>
  let usageTimer: ReturnType<typeof setInterval>

  onMounted(async () => {
    devices.value = await fetchDevices()
    if (devices.value.length > 0) {
      selectedDevice.value = devices.value[0]
    }

    // 状态轮询：每 5 秒
    statusTimer = setInterval(() => {
      if (isToday.value) loadStatus()
    }, 5_000)

    // 使用数据轮询：每 30 秒（仅今天）
    usageTimer = setInterval(() => {
      if (isToday.value) loadUsage()
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
    selectedDate,
    loading,
    isToday,
    isAlive,
    currentApp,
    lastSeenStr,
    appSummaries,
    totalSeconds,
    maxSeconds,
    activeHours,
  }
}
