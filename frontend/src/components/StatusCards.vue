<script setup lang="ts">
import { computed } from 'vue'
import { formatDuration } from '../composables/useHeartbeat'
import AppIcon from './AppIcon.vue'
import DashboardCard from './DashboardCard.vue'

const props = defineProps<{
  username: string
  isToday: boolean
  isAlive: boolean
  lastSeenStr: string
  lastSeenTitle: string
  loading: boolean
  failed: boolean
  appSummaries: { appId: number; appName: string; totalSeconds: number }[]
  totalSeconds: number
  awaySeconds: number
  /** 在线并集:滤掉 away 后跨设备去重的墙钟时长,答"我今天在多久" */
  onlineSeconds: number
  hasConcurrentUse: boolean
  isAllDevices: boolean
  includeAway: boolean
}>()

// 主数字用并集(人只有一个),求和降为副数字。单设备时两者相等,只显示一个。
const showSumAsSecondary = computed(() =>
  props.isAllDevices && props.hasConcurrentUse && props.totalSeconds > props.onlineSeconds
)
</script>

<template>
  <section class="mb-6 grid grid-cols-[repeat(auto-fit,minmax(220px,1fr))] gap-4 max-[640px]:grid-cols-1">
    <!-- 死了吗 -->
    <DashboardCard class="mb-0 gap-1.5">
      <span class="text-sm font-medium text-muted-foreground">死了吗</span>
      <span
        class="text-[1.75rem] font-bold"
        :class="isToday && !loading ? (isAlive ? 'text-alive' : 'text-dead') : 'text-muted-foreground'"
      >
        {{ loading ? '看看呢…' : isToday ? (isAlive ? '还活着' : '似了喵') : '--' }}
      </span>
      <span
        v-if="lastSeenStr && isToday && !isAlive && !loading"
        class="text-[0.8rem] text-muted-foreground"
        :title="lastSeenTitle"
      >
        最后活跃 {{ lastSeenStr }}
      </span>
    </DashboardCard>

    <!-- 本次存活 -->
    <DashboardCard class="mb-0 gap-1.5">
      <span class="text-sm font-medium text-muted-foreground">本次存活</span>
      <!-- 主数字 = 在线并集:两台机同时开着不算两份人生 -->
      <span
        class="font-mono text-[1.75rem] font-bold text-foreground"
        :title="showSumAsSecondary ? '跨设备去重后的实际在线时长' : undefined"
      >{{ loading && !appSummaries.length ? '加载中…' : failed && !appSummaries.length ? '—' : onlineSeconds === 0 ? '0m' : formatDuration(onlineSeconds) }}</span>
      <span class="text-[0.8rem] text-muted-foreground">
        {{ appSummaries.length }} 个应用<!--
        --><template v-if="showSumAsSecondary"> · <span title="各设备时长求和,并发使用会超过实际在线时长">屏幕占用 {{ formatDuration(totalSeconds) }}</span></template><!--
        --><template v-if="awaySeconds > 0"> · <span title="设备开着但人不在(息屏/睡眠/锁屏),各设备求和">{{ includeAway ? '含' : '另有' }}空转 {{ formatDuration(awaySeconds) }}</span></template>
      </span>
    </DashboardCard>

    <!-- 今日最爱 -->
    <DashboardCard class="mb-0 gap-1.5">
      <span class="text-sm font-medium text-muted-foreground">{{ isToday ? '今日最爱' : '当日最爱' }}</span>
      <span v-if="appSummaries[0]" class="flex items-center gap-2 text-[1.25rem] font-bold text-foreground">
        <AppIcon
          :username="username"
          :app-id="appSummaries[0].appId"
          class="h-6 w-6 rounded object-contain"
        />
        <span class="truncate">{{ appSummaries[0].appName }}</span>
      </span>
      <span v-else class="text-[1.25rem] font-bold text-muted-foreground">--</span>
      <span class="text-[0.8rem] text-muted-foreground" v-if="appSummaries[0]">
        沉迷时长 {{ formatDuration(appSummaries[0].totalSeconds) }}
      </span>
    </DashboardCard>
  </section>
</template>
