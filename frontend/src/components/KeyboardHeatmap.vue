<script setup lang="ts">
import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import DashboardCard from './DashboardCard.vue'
import { KEYBOARD_ROWS } from '@/keyboard/keyPositions'

const props = defineProps<{
  loading?: boolean
  keyFrequency: { code: number; count: number }[]
}>()

const countByCode = computed(() => {
  const m = new Map<number, number>()
  for (const k of props.keyFrequency) m.set(k.code, k.count)
  return m
})

const maxCount = computed(() => {
  let max = 0
  for (const k of props.keyFrequency) if (k.count > max) max = k.count
  return max
})

const totalCount = computed(() => props.keyFrequency.reduce((s, k) => s + k.count, 0))

function countFor(code: number): number {
  return countByCode.value.get(code) ?? 0
}

// 0 → 透明，按比例插值到 primary 色的不透明度
function intensityStyle(code: number): Record<string, string> {
  const c = countFor(code)
  if (c === 0 || maxCount.value === 0) return {}
  const t = c / maxCount.value
  // 用对数缓和，避免高频键一枝独秀把其它全压成透明
  const alpha = 0.12 + 0.88 * Math.sqrt(t)
  return { backgroundColor: `color-mix(in srgb, var(--primary) ${Math.round(alpha * 100)}%, transparent)` }
}

const hovered = ref<{ label: string; code: number; count: number } | null>(null)

// ── 趣味换算 ──
// 每条都用"≈"，基准标注在注释里，避免显得精确。
type FunFact = { text: string }

const funFacts = computed<FunFact[]>(() => {
  const n = totalCount.value
  if (n === 0) return []

  const facts: FunFact[] = []

  // 《三体》三部曲约 88 万字
  const santi = n / 880_000
  if (santi >= 0.01) {
    facts.push({
      text: `相当于敲出了 ${santi >= 1 ? santi.toFixed(1) : santi.toFixed(2)} 部《三体》三部曲`,
    })
  }

  // 代码行数：每行约 30 字符
  const lines = Math.round(n / 30)
  facts.push({ text: `约等于写了 ${lines.toLocaleString()} 行代码` })

  // 卡路里：1h 打字约 30 cal，60 WPM × 6 字符/词 ≈ 21600 次/h
  //   → 每次 ≈ 0.00000139 kCal（小到可忽略，正是笑点）
  const kcal = n * 0.00000139
  facts.push({ text: `手指燃烧了约 ${kcal.toFixed(4)} kCal` })

  // 手指键程：每次按下约 8mm
  const meters = n * 0.008
  if (meters >= 1000) {
    facts.push({ text: `手指累计移动约 ${(meters / 1000).toFixed(2)} 公里` })
  } else {
    facts.push({ text: `手指累计移动约 ${Math.round(meters)} 米` })
  }

  // 莎士比亚全集约 88.4 万单词 ≈ 按 5 字符/词算 442 万字符
  const shakespeare = n / 4_420_000
  if (shakespeare >= 0.01) {
    facts.push({
      text: `约是莎士比亚全集的 ${(shakespeare * 100).toFixed(0)}%`,
    })
  }

  return facts
})

const factIndex = ref(0)
const currentFact = computed(() => funFacts.value[factIndex.value] ?? null)

function nextFact() {
  if (funFacts.value.length === 0) return
  factIndex.value = (factIndex.value + 1) % funFacts.value.length
}

let rotateTimer: ReturnType<typeof setInterval> | undefined
onMounted(() => {
  rotateTimer = setInterval(nextFact, 4000)
})
onUnmounted(() => clearInterval(rotateTimer))

// 数据变化时若当前下标越界则归零
watch(funFacts, (facts) => {
  if (factIndex.value >= facts.length) factIndex.value = 0
})
</script>

<template>
  <DashboardCard>
    <div class="flex items-baseline justify-between">
      <h2 class="text-sm font-semibold text-foreground">键盘热力图</h2>
      <span class="font-mono text-[0.75rem] text-muted-foreground">
        <template v-if="hovered">{{ hovered.label }} · {{ hovered.count.toLocaleString() }} 次</template>
        <template v-else>共 {{ totalCount.toLocaleString() }} 次按键</template>
      </span>
    </div>

    <div v-if="totalCount > 0" class="flex flex-col gap-1.5 overflow-x-auto">
      <div
        v-for="(row, ri) in KEYBOARD_ROWS"
        :key="ri"
        class="flex gap-1.5"
      >
        <div
          v-for="(key, ki) in row"
          :key="ki"
          class="relative flex h-9 min-w-0 shrink-0 items-center justify-center rounded-md border border-border/50 bg-secondary/40 text-[0.7rem] font-medium text-foreground/80 transition-colors"
          :style="{ flexGrow: key.w ?? 1, flexBasis: `${(key.w ?? 1) * 2.2}rem`, ...intensityStyle(key.code) }"
          @mouseenter="hovered = { label: key.label, code: key.code, count: countFor(key.code) }"
          @mouseleave="hovered = null"
        >
          {{ key.label }}
        </div>
      </div>
    </div>

    <button
      v-if="currentFact"
      type="button"
      class="group flex items-center gap-2 self-start rounded-full border border-border/50 bg-secondary/30 px-3 py-1.5 text-left text-[0.8rem] text-foreground/80 transition-colors hover:bg-accent"
      title="点击切换"
      @click="nextFact"
    >
      <span>{{ currentFact.text }}</span>
      <span class="font-mono text-[0.65rem] text-muted-foreground opacity-0 transition-opacity group-hover:opacity-100">↻</span>
    </button>

    <div v-else-if="totalCount === 0" class="py-8 text-center text-[0.9rem] text-muted-foreground">{{ loading ? '正在读取按键统计…' : '当日暂无按键记录' }}</div>
  </DashboardCard>
</template>
