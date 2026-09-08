import { ref, computed, onMounted, onUnmounted, type Ref } from 'vue'
import type { ApiError, DeviceInfoResponse, DeviceStatusResponse } from '../api/index'
import { fetchPublicDeviceStatus, toApiError } from '../api/index'
import { formatExactLocalDateTime, formatLastSeen, latestDate } from '../lib/lastSeen'

/** 一台设备的在场事实。供当前使用面板展示。 */
export interface DevicePresence {
  deviceId: number
  deviceName: string
  isOnline: boolean
  currentApp: string | null
  currentAppId: number | null
  currentAppKey: string | null
  currentAppIdentityKey: string | null
  lastSeen: Date | null
}

/**
 * 在场域：per-device 实时状态（是否在线、当前应用、最后活跃时间）。
 * 自带 5s 轮询（仅在查看今天时刷新）。与报表的 30s 轮询生命周期独立。
 *
 * 多设备：状态端点是 per-device 路径,聚合视图下前端并发拉 N 台（N 为个位数,
 * 5s 多一两个请求可忽略,服务端零改动）。单台失败不影响其余（allSettled）。
 * 不做的：把 N 台的"当前应用"合成一个值——双机并发时那不是一个值。
 */
export function useDeviceStatus(
  username: string,
  devices: Ref<DeviceInfoResponse[]>,
  selectedDevice: Ref<number>,
  isToday: Ref<boolean>,
) {
  const statusMap = ref<Map<number, DeviceStatusResponse>>(new Map())
  const error = ref<ApiError | null>(null)

  /** 聚合视图（0）拉全部设备；选中单台只拉那台。 */
  const targetDeviceIds = computed<number[]>(() => {
    if (selectedDevice.value) return [selectedDevice.value]
    return devices.value.map(d => d.id!).filter(id => id != null)
  })

  /** per-device 在场行。在线的排前面，其余按设备列表顺序。 */
  const presences = computed<DevicePresence[]>(() => {
    const rows: DevicePresence[] = []
    for (const id of targetDeviceIds.value) {
      const s = statusMap.value.get(id)
      const name = devices.value.find(d => d.id === id)?.name ?? `设备 ${id}`
      const online = isToday.value && (s?.isOnline ?? false)
      const app = online ? (s?.currentAppDisplayName ?? s?.currentApp ?? null) : null
      rows.push({
        deviceId: id,
        deviceName: name,
        isOnline: online,
        currentApp: app,
        currentAppId: online ? (s?.currentAppId ?? null) : null,
        currentAppKey: online ? (s?.currentAppKey ?? null) : null,
        currentAppIdentityKey: online ? (s?.currentAppIdentityKey ?? null) : null,
        lastSeen: s?.lastSeen ?? null,
      })
    }
    return rows.sort((a, b) => Number(b.isOnline) - Number(a.isOnline))
  })

  const onlinePresences = computed(() => presences.value.filter(p => p.isOnline))

  /** 任一设备在线即"在场"。 */
  const isAlive = computed(() => onlinePresences.value.length > 0)

  /** 聚合视图取所选范围内真正最近的一次心跳，而不是设备列表第一项。 */
  const lastSeen = computed(() => latestDate(presences.value.map(p => p.lastSeen)))
  const lastSeenStr = computed(() => formatLastSeen(lastSeen.value))
  const lastSeenTitle = computed(() => formatExactLocalDateTime(lastSeen.value))

  let loadSequence = 0

  async function load(commitIf: () => boolean = () => true) {
    const sequence = ++loadSequence
    const ids = targetDeviceIds.value
    if (ids.length === 0) return
    const results = await Promise.allSettled(
      ids.map(id => fetchPublicDeviceStatus(username, id).then(s => [id, s] as const)),
    )
    const next = new Map<string | number, DeviceStatusResponse>()
    let lastErr: unknown = null
    for (const r of results) {
      if (r.status === 'fulfilled') next.set(r.value[0], r.value[1])
      else lastErr = r.reason
    }
    if (sequence !== loadSequence || !commitIf()) return
    statusMap.value = next as Map<number, DeviceStatusResponse>
    // 全军覆没才算错误：部分设备探测失败不该让在场卡整体亮红。
    error.value = next.size === 0 && lastErr !== null ? toApiError(lastErr) : null
  }

  let timer: ReturnType<typeof setInterval>
  onMounted(() => {
    timer = setInterval(() => {
      if (isToday.value) load()
    }, 5_000)
  })
  onUnmounted(() => clearInterval(timer))

  return {
    presences,
    onlinePresences,
    error,
    isAlive,
    lastSeenStr,
    lastSeenTitle,
    load,
  }
}
