<script setup lang="ts">
import { useHeartbeat, formatDuration } from './composables/useHeartbeat'
import { getIconUrl } from './api'

const {
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
} = useHeartbeat()
</script>

<template>
  <div class="dashboard">
    <header class="header">
      <div class="logo">
        <span class="dot" :class="{ alive: isAlive }"></span>
        <span>-QuQ-</span>
      </div>
      <span class="card-label">你在视奸我，对吧！</span>
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
          <span class="card-label">死了吗</span>
          <span
            class="card-value status"
            :class="isToday ? (isAlive ? 'alive' : 'dead') : 'off'"
          >
            {{ isToday ? (isAlive ? '还活着' : '似了喵') : '--' }}
          </span>
          <span class="card-sub" v-if="lastSeenStr && isToday">
            最后活跃 {{ lastSeenStr }}
          </span>
        </div>
        <div class="card">
          <span class="card-label">本次存活</span>
          <span class="card-value accent" style="color: var(--text);">{{ formatDuration(totalSeconds) }}</span>
          <span class="card-sub">{{ appSummaries.length }} 个应用</span>
        </div>
        <div class="card">
          <span class="card-label">今日最爱</span>
          <span class="card-value accent top-app" v-if="appSummaries[0]" style="color: var(--text);">
            <img
              :src="getIconUrl(appSummaries[0].appName)"
              class="top-app-icon"
              @error="($event.target as HTMLImageElement).style.display = 'none'"
            />
            {{ appSummaries[0].appName }}
          </span>
          <span class="card-value accent top-app" v-else>--</span>
          <span class="card-sub" v-if="appSummaries[0]">
            沉迷时长 {{ formatDuration(appSummaries[0].totalSeconds) }}
          </span>
        </div>
      </section>

      <!-- 当前使用 -->
      <section class="panel current-app-panel" v-if="isToday">
        <h2>当前使用</h2>
        <div class="current-app" v-if="isAlive && currentApp">
          <span class="current-dot alive"></span>
          <img
            :src="getIconUrl(currentApp)"
            class="current-icon"
            @error="($event.target as HTMLImageElement).style.display = 'none'"
          />
          <span class="current-name">{{ currentApp }}</span>
        </div>
        <div class="current-app offline" v-else-if="!isAlive">
          <span class="current-dot"></span>
          <span class="current-name dim">设备离线</span>
        </div>
        <div class="current-app" v-else>
          <span class="current-dot alive"></span>
          <span class="current-name dim">无前台应用</span>
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
  font-family: 'Cascadia Code', 'Microsoft YaHei', 'SF Mono', 'Consolas', monospace;
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

/* Current App Panel */
.current-app {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.25rem 0;
}

.current-dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  background: #444;
  flex-shrink: 0;
}

.current-dot.alive {
  background: var(--alive);
  box-shadow: 0 0 8px var(--alive);
  animation: pulse 2s ease-in-out infinite;
}

.current-icon {
  width: 28px;
  height: 28px;
  border-radius: 6px;
  object-fit: contain;
  flex-shrink: 0;
}

.current-name {
  font-size: 1.1rem;
  font-weight: 600;
}

.current-name.dim {
  color: var(--text-dim);
  font-weight: 400;
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
