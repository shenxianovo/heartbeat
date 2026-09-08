import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import type { ApiError, AppInfoResponse, KeyFrequencyItem, InputCountsResponse } from '../api/index'
import {
  fetchAdminAppCatalog,
  fetchMe,
  fetchPublicApps,
  fetchPublicKeyFrequency,
  fetchPublicInputCounts,
} from '../api/index'
import { loadAdminProvisionalAppIds } from '../appCatalog/adminOverlay'
import { authStore } from '../stores/auth'
import { runForCurrentIdentity, useAsyncData } from './useAsyncData'
import { useDeviceSelection } from './useDeviceSelection'
import { useDeviceStatus } from './useDeviceStatus'
import { useReports } from './useReports'
import { CalendarContextError, resolveCalendarContext } from '../calendar/localCalendarWindow'

export function formatDuration(sec: number): string {
  const h = Math.floor(sec / 3600)
  const m = Math.floor((sec % 3600) / 60)
  if (h > 0) return `${h}h ${m}m`
  if (m > 0) return `${m}m`
  return '< 1m'
}

/**
 * Dashboard 的瘦协调器：持有应用元数据，组合设备选择 / 在场 / 报表三个数据域，
 * 编排 30s 报表轮询与 device/date 变更时的统一刷新。
 */
export function useHeartbeat(username: string) {
  const selection = useDeviceSelection(username)
  const { selectedDevice, selectedDate } = selection
  const calendarContext = ref(resolveCalendarContext(selectedDate.value))
  const refreshIdentity = ref(calendarContext.value.correlationIdentity)
  const calendarValid = ref(true)
  const calendarError = ref<ApiError | null>(null)
  const isToday = computed(() => calendarValid.value && calendarContext.value.isToday)

  const appsData = useAsyncData<AppInfoResponse[]>(() => fetchPublicApps(username), [])
  const apps = appsData.data
  const provisionalAppIds = ref<Set<number>>(new Set())
  const loading = ref(true)

  const appNameMap = computed(() => {
    const map = new Map<number, string>()
    for (const app of apps.value) map.set(app.id!, app.displayName ?? app.name ?? `App ${app.id}`)
    return map
  })

  const status = useDeviceStatus(username, selection.devices, selectedDevice, isToday)
  const reports = useReports(username, selectedDevice, calendarContext, refreshIdentity)

  const inputWindow = () => ({ deviceId: selectedDevice.value,
    start: calendarContext.value.day.start, end: calendarContext.value.day.endExclusive })
  const inputScope = () => [selectedDevice.value, calendarContext.value.day.start,
    calendarContext.value.day.endExclusive, calendarContext.value.day.timeZone].join('|')
  const kf = useAsyncData<KeyFrequencyItem[]>(() => fetchPublicKeyFrequency(username, inputWindow()), [], inputScope)
  const counts = useAsyncData<InputCountsResponse | null>(() => fetchPublicInputCounts(username, inputWindow()), null, inputScope)
  const keyFrequency = kf.data
  // 键盘与鼠标共享查询窗口，独立提交结果；一项失败不清空另一项。
  const loadInput = () => Promise.all([kf, counts].map(source =>
    runForCurrentIdentity(source, () => refreshIdentity.value)))

  async function loadAdminOverlay(commitIf: () => boolean = () => true) {
    try {
      const next = await loadAdminProvisionalAppIds(username, {
        isAuthenticated: authStore.isAuthenticated,
        currentUsername: authStore.username.value,
        fetchMe,
        fetchInventory: fetchAdminAppCatalog,
      })
      if (commitIf()) provisionalAppIds.value = next
    } catch {
      // 管理员标记是附加信息；失败不能让普通 Dashboard 取数整体失败。
      if (commitIf()) provisionalAppIds.value = new Set()
    }
  }

  // 任一数据域出错就点亮:UI 据此区分"出错"与"这天没数据"。
  const error = computed(() =>
    calendarError.value
    ?? selection.error.value
    ?? appsData.error.value
    ?? status.error.value
    ?? reports.error.value
    ?? kf.error.value,
  )

  const timezoneLabel = computed(() =>
    calendarValid.value ? calendarContext.value.displayLabel : '日历窗口不可用',
  )

  function captureCalendarContext(): boolean {
    try {
      calendarContext.value = resolveCalendarContext(selectedDate.value)
      refreshIdentity.value = calendarContext.value.correlationIdentity
      calendarValid.value = true
      calendarError.value = null
      return true
    } catch (error) {
      if (error instanceof CalendarContextError) {
        // 没有有效 Context 也必须推进 generation，作废上一日期仍在途的普通响应。
        refreshIdentity.value = globalThis.crypto.randomUUID()
        calendarValid.value = false
        loading.value = false
        calendarError.value = { kind: 'calendar', code: error.code, message: error.message }
        return false
      }
      throw error
    }
  }

  async function loadDashboardDataForCapturedContext() {
    loading.value = true
    const expectedIdentity = refreshIdentity.value
    const isCurrent = () => refreshIdentity.value === expectedIdentity
    try {
      // 取数不再等设备列表：默认 deviceId=0 即聚合查询。
      // 设备列表只影响选择器选项与 presence 目标,由 selection.reload() 独立拉。
      await Promise.all([
        runForCurrentIdentity(appsData, () => refreshIdentity.value),
        reports.loadUsage(),
        status.load(isCurrent),
        reports.loadDaily(),
        reports.loadWeekly(),
        loadInput(),
        loadAdminOverlay(isCurrent),
      ])
    } finally {
      if (isCurrent()) loading.value = false
    }
  }

  async function refresh() {
    // 一次 refresh 只捕获一次浏览器 civil timezone；以下并发请求共享同一不可变 context。
    if (!captureCalendarContext()) return
    await loadDashboardDataForCapturedContext()
  }

  let usageTimer: ReturnType<typeof setInterval>

  onMounted(async () => {
    // setup 已为首屏刷新捕获一次 context；这里直接消费它，避免子组件与报表各见一个 generation。
    // 默认选中值恒为"全部设备",watch 不会因 0→N 触发,首屏必须显式加载一次。
    await loadDashboardDataForCapturedContext()

    usageTimer = setInterval(() => {
      // 报表 poll 属于当前 generation：复用其时区快照，避免长 Recap SSE 每 30s 被隐式作废。
      // 时区变化与 today/historical 重新判定都在下一次显式 refresh 采纳。
      if (!isToday.value) return
      reports.loadUsage()
      reports.loadDaily()
      reports.loadWeekly()
      loadInput()
    }, 30_000)
  })

  onUnmounted(() => clearInterval(usageTimer))

  watch([selectedDevice, selectedDate], () => refresh())

  return {
    devices: selection.devices,
    error,
    refresh,
    selectedDevice,
    selectedDeviceName: selection.selectedDeviceName,
    selectedDate,
    usageData: reports.usageData,
    appNameMap,
    provisionalAppIds,
    loading,
    isToday,
    isAlive: status.isAlive,
    presences: status.presences,
    onlinePresences: status.onlinePresences,
    lastSeenStr: status.lastSeenStr,
    lastSeenTitle: status.lastSeenTitle,
    isAllDevices: selection.isAllDevices,
    appSummaries: reports.appSummaries,
    totalSeconds: reports.totalSeconds,
    usageSeconds: reports.usageSeconds,
    awaySeconds: reports.awaySeconds,
    onlineSeconds: reports.onlineSeconds,
    perDeviceSeconds: reports.perDeviceSeconds,
    hasConcurrentUse: reports.hasConcurrentUse,
    maxSeconds: reports.maxSeconds,
    weeklyAppSummaries: reports.weeklyAppSummaries,
    weeklyTotalSeconds: reports.weeklyTotalSeconds,
    weeklyAwaySeconds: reports.weeklyAwaySeconds,
    includeAway: reports.includeAway,
    keyFrequency,
    inputCounts: counts.data,
    inputCountsLoading: counts.pending,
    inputCountsFailed: computed(() => counts.error.value !== null),
    calendarContext,
    calendarValid,
    timezoneLabel,
  }
}
