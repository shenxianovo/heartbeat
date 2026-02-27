<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import type { AppSummary } from './types'
import { fetchDevices, fetchUsage, getIconUrl } from './api'
import type { AppUsage } from './types'

function localDateStr(): string {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

const devices = ref<string[]>([])
const selectedDevice = ref('')
const selectedDate = ref(localDateStr())
const usageData = ref<AppUsage[]>([])
const loading = ref(false)

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

const maxSeconds = computed(() =>
  appSummaries.value[0]?.totalSeconds ?? 1
)

const lastActive = computed(() => {
  if (usageData.value.length === 0) return null
  const ms = Math.max(...usageData.value.map(u => new Date(u.endTime).getTime()))
  return new Date(ms)
})

const isToday = computed(() =>
  selectedDate.value === localDateStr()
)

const isAlive = computed(() => {
  if (!isToday.value || !lastActive.value) return false
  return Date.now() - lastActive.value.getTime() < 5 * 60 * 1000
})

const lastActiveStr = computed(() => {
  if (!lastActive.value) return ''
  return lastActive.value.toLocaleTimeString('zh-CN', {
    hour: '2-digit', minute: '2-digit'
  })
})

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

function formatDuration(sec: number): string {
  const h = Math.floor(sec / 3600)
  const m = Math.floor((sec % 3600) / 60)
  if (h > 0) return `${h}h ${m}m`
  if (m > 0) return `${m}m`
  return '< 1m'
}

async function loadData() {
  if (!selectedDevice.value) return
  loading.value = true
  try {
    usageData.value = await fetchUsage(selectedDevice.value, selectedDate.value)
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  devices.value = await fetchDevices()
  if (devices.value.length > 0) {
    selectedDevice.value = devices.value[0]
  }
})

watch([selectedDevice, selectedDate], () => loadData())

// 如果查看的是今天，每 30s 自动刷新
setInterval(() => {
  if (isToday.value) loadData()
}, 30_000)
</script>

<template>
  <div class="dashboard">
    <header class="header">
      <div class="logo">
        <span class="dot" :class="{ alive: isAlive }"></span>
        <span>heartbeat</span>
      </div>
      <div class="controls">
        <select v-model="selectedDevice" class="ctl">
          <option v-for="d in devices" :key="d" :value="d">{{ d }}</option>
        </select>
        <input type="date" v-model="selectedDate" class="ctl" />
      </div>
    </header>

    <main>
      <!-- 状态卡片 -->
      <section class="cards">
        <div class="card">
          <span class="card-label">状态</span>
          <span
            class="card-value status"
            :class="isToday ? (isAlive ? 'alive' : 'dead') : 'off'"
          >
            {{ isToday ? (isAlive ? 'ALIVE' : 'DEAD') : '--' }}
          </span>
          <span class="card-sub" v-if="lastActive && isToday">
            最后活跃 {{ lastActiveStr }}
          </span>
        </div>
        <div class="card">
          <span class="card-label">总应用时长</span>
          <span class="card-value accent">{{ formatDuration(totalSeconds) }}</span>
          <span class="card-sub">{{ appSummaries.length }} 个应用</span>
        </div>
        <div class="card">
          <span class="card-label">最常使用</span>
          <span class="card-value accent top-app" v-if="appSummaries[0]">
            <img
              :src="getIconUrl(appSummaries[0].appName)"
              class="top-app-icon"
              @error="($event.target as HTMLImageElement).style.display = 'none'"
            />
            {{ appSummaries[0].appName }}
          </span>
          <span class="card-value accent top-app" v-else>--</span>
          <span class="card-sub" v-if="appSummaries[0]">
            {{ formatDuration(appSummaries[0].totalSeconds) }}
          </span>
        </div>
      </section>

      <!-- 活动时间线 -->
      <section class="panel">
        <h2>活动时间线</h2>
        <div class="timeline">
          <div
            v-for="h in 24"
            :key="h - 1"
            class="tl-block"
            :class="{ active: activeHours.has(h - 1) }"
            :title="`${String(h - 1).padStart(2, '0')}:00`"
          ></div>
        </div>
        <div class="tl-labels">
          <span>00</span>
          <span>06</span>
          <span>12</span>
          <span>18</span>
          <span>24</span>
        </div>
      </section>

      <!-- 应用时长排行 -->
      <section class="panel">
        <h2>应用时长排行</h2>
        <div v-if="appSummaries.length" class="ranking">
          <div v-for="(app, i) in appSummaries" :key="app.appName" class="rank-row">
            <div class="rank-meta">
              <span class="rank-i">{{ i + 1 }}</span>
              <img
                :src="getIconUrl(app.appName)"
                class="rank-icon"
                @error="($event.target as HTMLImageElement).style.display = 'none'"
              />
              <span class="rank-name">{{ app.appName }}</span>
              <span class="rank-dur">{{ formatDuration(app.totalSeconds) }}</span>
            </div>
            <div class="bar-bg">
              <div
                class="bar"
                :style="{ width: `${(app.totalSeconds / maxSeconds) * 100}%` }"
              ></div>
            </div>
          </div>
        </div>
        <div v-else class="empty">暂无数据</div>
      </section>
    </main>

    <div v-if="loading" class="loading-bar"></div>
  </div>
</template>

<style scoped>
.dashboard {
  max-width: 860px;
  margin: 0 auto;
  padding: 2rem 1.5rem;
  position: relative;
}

/* Header */
.header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 2rem;
}

.logo {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  font-size: 1.5rem;
  font-weight: 700;
  letter-spacing: -0.02em;
  user-select: none;
}

.dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  background: #444;
  transition: background 0.3s;
}

.dot.alive {
  background: var(--alive);
  box-shadow: 0 0 8px var(--alive);
  animation: pulse 2s ease-in-out infinite;
}

@keyframes pulse {
  0%, 100% { opacity: 1; box-shadow: 0 0 8px var(--alive); }
  50% { opacity: 0.4; box-shadow: 0 0 2px var(--alive); }
}

.controls {
  display: flex;
  gap: 0.75rem;
}

.ctl {
  background: var(--bg-card);
  border: 1px solid var(--border);
  color: var(--text);
  padding: 0.5rem 0.75rem;
  border-radius: 6px;
  font-size: 0.875rem;
  outline: none;
  cursor: pointer;
  transition: border-color 0.2s;
}

.ctl:focus {
  border-color: var(--accent);
}

/* Cards */
.cards {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 1rem;
  margin-bottom: 1.5rem;
}

.card {
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 1.25rem;
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}

.card-label {
  font-size: 0.75rem;
  color: var(--text-dim);
  text-transform: uppercase;
  letter-spacing: 0.06em;
}

.card-value {
  font-size: 1.75rem;
  font-weight: 700;
  font-family: 'SF Mono', 'Cascadia Code', 'Consolas', monospace;
}

.card-value.accent {
  color: var(--accent);
}

.card-value.top-app {
  font-size: 1.25rem;
  font-family: inherit;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.top-app-icon {
  width: 24px;
  height: 24px;
  border-radius: 4px;
  object-fit: contain;
}

.card-value.status.alive {
  color: var(--alive);
}

.card-value.status.dead {
  color: var(--dead);
}

.card-value.status.off {
  color: var(--text-dim);
}

.card-sub {
  font-size: 0.8rem;
  color: var(--text-dim);
}

/* Panel */
.panel {
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 1.25rem;
  margin-bottom: 1.5rem;
}

.panel h2 {
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--text-dim);
  text-transform: uppercase;
  letter-spacing: 0.06em;
  margin-bottom: 1rem;
}

/* Timeline */
.timeline {
  display: flex;
  gap: 2px;
  height: 28px;
}

.tl-block {
  flex: 1;
  background: #1f1f1f;
  border-radius: 3px;
  transition: background 0.3s;
}

.tl-block.active {
  background: var(--accent);
}

.tl-labels {
  display: flex;
  justify-content: space-between;
  margin-top: 6px;
  font-size: 0.65rem;
  color: var(--text-dim);
  font-family: 'SF Mono', 'Cascadia Code', 'Consolas', monospace;
}

/* Ranking */
.ranking {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.rank-meta {
  display: flex;
  align-items: center;
  margin-bottom: 0.3rem;
}

.rank-i {
  width: 1.5rem;
  color: var(--text-dim);
  font-size: 0.8rem;
  font-family: 'SF Mono', 'Cascadia Code', 'Consolas', monospace;
}

.rank-icon {
  width: 20px;
  height: 20px;
  margin-right: 0.5rem;
  border-radius: 4px;
  object-fit: contain;
  flex-shrink: 0;
}

.rank-name {
  flex: 1;
  font-size: 0.9rem;
}

.rank-dur {
  font-size: 0.8rem;
  color: var(--text-dim);
  font-family: 'SF Mono', 'Cascadia Code', 'Consolas', monospace;
}

.bar-bg {
  height: 5px;
  background: #1f1f1f;
  border-radius: 3px;
  overflow: hidden;
}

.bar {
  height: 100%;
  background: linear-gradient(90deg, var(--accent), var(--accent-sub));
  border-radius: 3px;
  transition: width 0.5s ease;
}

.empty {
  text-align: center;
  color: var(--text-dim);
  padding: 2rem;
  font-size: 0.9rem;
}

/* Loading bar */
.loading-bar {
  position: fixed;
  top: 0;
  left: 0;
  width: 100%;
  height: 2px;
  background: var(--accent);
  animation: loading 1s ease-in-out infinite;
}

@keyframes loading {
  0% { transform: scaleX(0); transform-origin: left; }
  50% { transform: scaleX(1); transform-origin: left; }
  51% { transform-origin: right; }
  100% { transform: scaleX(0); transform-origin: right; }
}

/* Responsive */
@media (max-width: 640px) {
  .header {
    flex-direction: column;
    gap: 1rem;
    align-items: flex-start;
  }

  .cards {
    grid-template-columns: 1fr;
  }

  .dashboard {
    padding: 1.5rem 1rem;
  }
}
</style>
