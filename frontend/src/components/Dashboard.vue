<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useHeartbeat } from '../composables/useHeartbeat'
import { authStore } from '../stores/auth'
import ActivityTimeline from './ActivityTimeline.vue'
import RecapCard from './RecapCard.vue'
import StrandQuestions from './StrandQuestions.vue'
import StatusCards from './StatusCards.vue'
import CurrentAppPanel from './CurrentAppPanel.vue'
import TodayRanking from './TodayRanking.vue'
import WeeklyChart from './WeeklyChart.vue'
import KeyboardHeatmap from './KeyboardHeatmap.vue'
import MouseActivity from './MouseActivity.vue'
import AppDetailModal from './AppDetailModal.vue'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import DatePicker from './DatePicker.vue'
import type { CalendarWindowEnvelope } from '../calendar/localCalendarWindow'

const props = defineProps<{ username: string }>()

const isOwnProfile = computed(() =>
  authStore.isAuthenticated && authStore.username.value === props.username
)

const {
  devices,
  error,
  refresh,
  selectedDevice,
  selectedDate,
  usageData,
  appNameMap,
  provisionalAppIds,
  loading,
  isToday,
  isAlive,
  onlinePresences,
  lastSeenStr,
  lastSeenTitle,
  isAllDevices,
  appSummaries,
  totalSeconds,
  awaySeconds,
  onlineSeconds,
  hasConcurrentUse,
  maxSeconds,
  weeklyAppSummaries,
  weeklyTotalSeconds,
  includeAway,
  keyFrequency,
  inputCounts,
  inputCountsLoading,
  inputCountsFailed,
  calendarContext,
  calendarValid,
  timezoneLabel,
} = useHeartbeat(props.username)

// 取数失败的人话:区分断网 / 服务端错误 / 解析异常(见 api ApiError)。
const errorMessage = computed(() => {
  const e = error.value
  if (!e) return ''
  if (e.kind === 'network') return '网络连接失败，请检查网络后重试'
  if (e.kind === 'http') return `服务器返回错误（${e.status}），请稍后重试`
  if (e.kind === 'calendar') return `日历窗口错误（${e.code}）：${e.message}`
  return '数据解析失败，请重试'
})

// Reka UI Select 用字符串值，selectedDevice 是 number —— 用 computed 双向桥接
const selectedDeviceStr = computed({
  get: () => String(selectedDevice.value),
  set: (v: string) => { selectedDevice.value = Number(v) },
})

// 点击排行条目 → 全局全屏应用详情弹窗（回放多轨 + 标题明细）
interface SelectedAppDetail {
  app: { appId: number; appName: string; totalSeconds: number }
  dayWindow: CalendarWindowEnvelope<'day'>
}

const selectedApp = ref<SelectedAppDetail | null>(null)

function openAppDetail(app: SelectedAppDetail['app']) {
  selectedApp.value = { app, dayWindow: calendarContext.value.day }
}

watch(() => calendarContext.value.day, (current) => {
  const captured = selectedApp.value?.dayWindow
  if (!captured) return
  if (
    captured.localDate !== current.localDate
    || captured.timeZone !== current.timeZone
    || captured.start !== current.start
    || captured.endExclusive !== current.endExclusive
  ) {
    selectedApp.value = null
  }
})

</script>

<template>
  <!-- 半屏留白让滚到底时可以完整看见固定在背景的吉祥物。 -->
  <div class="relative z-10 mx-auto w-[min(100%,1400px)] px-[clamp(0.75rem,3vw,2.5rem)] py-[clamp(1rem,3vw,2.5rem)] pb-[50vh]">
    <header class="mb-[clamp(1.25rem,3vw,2rem)] flex flex-wrap items-center justify-between gap-x-4 gap-y-3 pr-12 max-[640px]:flex-col max-[640px]:items-stretch max-[640px]:pr-0">
      <div class="flex min-w-0 select-none flex-wrap items-center gap-x-3 gap-y-1.5 font-display text-[clamp(1.15rem,2.5vw,1.5rem)] font-bold tracking-tight max-[640px]:pr-12">
        <span class="status-dot" :class="{ alive: isToday && isAlive }"></span>
        <span class="whitespace-nowrap">{{ username }}</span>
      </div>

      <div class="flex flex-wrap items-center gap-2 max-[640px]:w-full">
        <Select v-model="selectedDeviceStr">
          <SelectTrigger class="glass-control h-auto min-w-[8rem] border-glass-border px-3 py-1.5 text-sm shadow-sm max-[640px]:flex-1">
            <SelectValue placeholder="选择设备" />
          </SelectTrigger>
          <SelectContent>
            <!-- 默认视图:跨设备聚合。单台活跃时自然退化成单设备看板。 -->
            <SelectItem value="0">全部设备</SelectItem>
            <SelectItem v-for="d in devices" :key="d.id" :value="String(d.id)">
              {{ d.name }}
            </SelectItem>
          </SelectContent>
        </Select>

        <DatePicker
          v-model="selectedDate"
          :context-label="timezoneLabel"
          class="max-[640px]:w-full"
          title="Local Calendar Window：所选日期按本次刷新捕获的浏览器 IANA 时区解释"
        />

        <button
          class="glass-control cursor-pointer whitespace-nowrap px-3 py-1.5 text-[0.8rem] transition-colors"
          :class="includeAway ? 'text-primary' : 'text-muted-foreground hover:text-foreground'"
          :title="includeAway ? '统计已包含离开时间（息屏/睡眠/锁屏）' : '统计不含离开时间，点击计入'"
          @click="includeAway = !includeAway"
        >{{ includeAway ? '含离开' : '不含离开' }}</button>

        <RouterLink
          v-if="isOwnProfile"
          to="/get-started"
          class="glass-control px-3 py-1.5 text-[0.8rem] text-muted-foreground no-underline hover:text-foreground"
        >客户端</RouterLink>
        <RouterLink
          v-if="isOwnProfile"
          to="/settings"
          class="glass-control px-3 py-1.5 text-[0.8rem] text-muted-foreground no-underline hover:text-foreground"
        >设置</RouterLink>
        <button
          v-if="authStore.isAuthenticated"
          class="glass-control px-3 py-1.5 text-[0.8rem] text-muted-foreground hover:text-foreground"
          @click="authStore.logout()"
        >登出</button>
        <button
          v-else
          class="glass-control px-3 py-1.5 text-[0.8rem] font-medium text-primary"
          @click="authStore.redirectToLogin()"
        >登录</button>
      </div>
    </header>

    <div
      v-if="errorMessage"
      role="alert"
      class="mb-4 flex items-center justify-between gap-3 rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-2.5 text-sm text-destructive"
    >
      <span>{{ errorMessage }}</span>
      <button
        class="glass-control shrink-0 cursor-pointer px-3 py-1 text-[0.8rem] font-medium text-destructive transition-colors"
        :disabled="loading"
        @click="refresh()"
      >重试</button>
    </div>

    <main v-if="calendarValid" :aria-busy="loading">
      <StatusCards
        :username="username"
        :isToday="isToday"
        :isAlive="isAlive"
        :lastSeenStr="lastSeenStr"
        :lastSeenTitle="lastSeenTitle"
        :loading="loading"
        :failed="!!error"
        :appSummaries="appSummaries"
        :totalSeconds="totalSeconds"
        :awaySeconds="awaySeconds"
        :onlineSeconds="onlineSeconds"
        :hasConcurrentUse="hasConcurrentUse"
        :isAllDevices="isAllDevices"
        :includeAway="includeAway"
      />

      <div class="grid grid-cols-1 gap-0 min-[900px]:grid-cols-[1fr_340px] min-[900px]:items-start min-[900px]:gap-5 min-[1200px]:grid-cols-[1fr_420px] min-[1200px]:gap-6">
        <div class="min-w-0">
          <CurrentAppPanel
            :username="username"
            :isToday="isToday"
            :presences="onlinePresences"
          />

          <!-- owner 可生成/重生成；公开访客只读已有缓存，不触发 LLM。 -->
          <RecapCard
            :calendarContext="calendarContext"
            :username="username"
            :canRegenerate="isOwnProfile"
          />

          <!-- Strand 提问面板（ADR-028）：owner-only，写知识 + 烧 LLM，无 public 版。 -->
          <StrandQuestions
            v-if="isOwnProfile"
            :calendarContext="calendarContext"
          />

          <ActivityTimeline
            :key="selectedDevice"
            :loading="loading"
            :username="username"
            :usageData="usageData"
            :appNameMap="appNameMap"
            :dayWindow="calendarContext.day"
            :isToday="isToday"
            :devices="devices"
            :isAllDevices="isAllDevices"
          />

          <KeyboardHeatmap :keyFrequency="keyFrequency" :loading="loading" />
          <MouseActivity :counts="inputCounts" :loading="inputCountsLoading" :failed="inputCountsFailed" @retry="refresh()" />
        </div>

        <div class="min-w-0 min-[900px]:sticky min-[900px]:top-4">
          <TodayRanking
            :loading="loading"
            :username="username"
            :appSummaries="appSummaries"
            :maxSeconds="maxSeconds"
            :provisionalAppIds="provisionalAppIds"
            @select="openAppDetail"
          />

          <WeeklyChart
            :loading="loading"
            :username="username"
            :weeklyAppSummaries="weeklyAppSummaries"
            :weeklyTotalSeconds="weeklyTotalSeconds"
          />
        </div>
      </div>
    </main>

    <AppDetailModal
      v-if="selectedApp && calendarValid"
      :username="username"
      :deviceId="selectedDevice"
      :calendarContext="calendarContext"
      :app="selectedApp.app"
      :usageData="usageData"
      :devices="devices"
      :isProvisional="provisionalAppIds.has(selectedApp.app.appId)"
      @close="selectedApp = null"
    />

    <div v-if="loading" class="loading-bar"></div>
  </div>
</template>

<style scoped>
.loading-bar {
  position: fixed;
  top: 0;
  left: 0;
  width: 100%;
  height: 2px;
  background: var(--primary);
  animation: loading 1s ease-in-out infinite;
}

@keyframes loading {
  0%   { transform: scaleX(0); transform-origin: left; }
  50%  { transform: scaleX(1); transform-origin: left; }
  51%  { transform-origin: right; }
  100% { transform: scaleX(0); transform-origin: right; }
}
</style>
