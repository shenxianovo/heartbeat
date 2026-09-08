<script setup lang="ts">
import { computed, nextTick, onUnmounted, ref, watch } from 'vue'
import {
  fetchDailyRecap, fetchPublicDailyRecap, streamDailyRecapGeneration, recapGenerationErrorMessage,
  type DailyRecapResponse, type RecapStreamHandlers,
} from '../api/index'
import { useAsyncData } from '../composables/useAsyncData'
import RecapCorrection from './RecapCorrection.vue'
import DashboardCard from './DashboardCard.vue'
import { sameCalendarWindow, type CalendarContext } from '../calendar/localCalendarWindow'

/**
 * 当日 Recap 卡片（ADR-023，读写按动词拆分随 ADR-042）。
 *
 * 读与生成是两个端点：GET 纯读（零 LLM、零写库），生成走 POST 的 SSE 流。owner 视角下
 * "从未生成"或"段数据已长出水位"（`segmentStale`）时自动发起一次生成——阈值判断留在服务端，
 * 这里只看布尔。`knowledgeStale` 维持现状：只作为判脏位存在，不自动生成。
 * 访客只读 owner 已生成的缓存，永不触发 LLM。
 */
const props = defineProps<{
  calendarContext: CalendarContext
  username: string
  canRegenerate: boolean
}>()

const recap = useAsyncData<DailyRecapResponse | null>(
  () => props.canRegenerate
    ? fetchDailyRecap({ window: props.calendarContext.day })
    : fetchPublicDailyRecap(props.username, { window: props.calendarContext.day }),
  null,
)

/** 流式生成的在途状态：累积正文、推理过程、可读失败原因、以及用于中止的 controller。 */
const streaming = ref(false)
const streamText = ref('')
/**
 * 累积的推理增量（ADR-042 §9）。思考模式的模型可能先吐上百秒 `reasoning_content` 才给出第一个
 * 正文 token，那段沉默在界面上跟卡死没有区别；显示它是为了给等待一个进度感。
 * 但它是过程不是产物：首个正文 delta 一到就让位给叙事，落定 / 失败 / 切日期一律清空。
 */
const thinkingText = ref('')
const streamError = ref('')
let controller: AbortController | null = null
/**
 * 每次生成一个序号：abort 之后在途的回调仍可能被 microtask 唤醒一次，
 * 序号对不上就丢弃——把旧流的 delta 写到已经换了日期的卡片上是 bug，不是取舍（ADR-042 §6）。
 */
let streamSeq = 0
let displayedWindow: CalendarContext['day'] | null = null
let displayedIdentity: string | null = null

// ===== 推理面板的滚动 =====
// 推理动辄上万字符，容器必须限高 + 自己滚，否则一条流就把卡片撑出屏幕。
const thinkingPanel = ref<HTMLElement | null>(null)
/** 距底 24px 内算"贴着底"：够宽容以容纳行高与亚像素误差，又不会把用户翻上去一行也当成贴底。 */
const STICK_TO_BOTTOM_PX = 24
/** 自动滚底只在用户自己没有翻阅时进行——被抢走滚动位置比不自动滚更烦人。 */
const stickToBottom = ref(true)

function onThinkingScroll() {
  const el = thinkingPanel.value
  if (!el) return
  stickToBottom.value = el.scrollTop + el.clientHeight >= el.scrollHeight - STICK_TO_BOTTOM_PX
}

// 新推理到达后滚到底。必须等 nextTick：DOM 还没长出新文本时 scrollHeight 是旧的，滚了也差一屏。
watch(thinkingText, async text => {
  if (!text || !stickToBottom.value) return
  await nextTick()
  const el = thinkingPanel.value
  if (el) el.scrollTop = el.scrollHeight
})

function abortStream() {
  controller?.abort()
  controller = null
}

async function consumeGenerationStream(
  window: CalendarContext['day'],
  signal: AbortSignal | undefined,
  handlers: RecapStreamHandlers,
): Promise<string> {
  let generationError = ''
  const captureError = (message: string) => {
    generationError = message
    handlers.onError?.(message)
  }
  try {
    await streamDailyRecapGeneration({ window, ...(signal ? { signal } : {}) }, {
      ...handlers,
      onError: captureError,
    })
  } catch (error) {
    captureError(recapGenerationErrorMessage(error))
  }
  return generationError
}

async function load() {
  const expectedIdentity = props.calendarContext.correlationIdentity
  await recap.run(() => props.calendarContext.correlationIdentity === expectedIdentity)
  if (props.calendarContext.correlationIdentity === expectedIdentity) autoGenerate()
}

/** 三态里的两种需要生成：从未生成、或已有叙事但段数据落后。空日与访客一律不生成。 */
function autoGenerate() {
  if (!props.canRegenerate || streaming.value) return
  const data = recap.data.value
  if (!data || data.isEmpty) return
  if (data.narrative == null || data.segmentStale) generate()
}

async function runGeneration(
  calendarContext: CalendarContext,
  abortOnRefresh: boolean,
): Promise<string> {
  const expectedIdentity = calendarContext.correlationIdentity
  const window = calendarContext.day
  if (!abortOnRefresh
    && props.calendarContext.correlationIdentity !== expectedIdentity) {
    return consumeGenerationStream(window, undefined, {})
  }

  abortStream()
  const seq = ++streamSeq
  const activeController = abortOnRefresh ? new AbortController() : null
  controller = activeController
  const isVisible = () => seq === streamSeq
    && props.calendarContext.correlationIdentity === expectedIdentity
  if (isVisible()) {
    streaming.value = true
    streamError.value = ''
    streamText.value = ''
    thinkingText.value = ''
    stickToBottom.value = true // 新一次生成从贴底开始，上一次用户翻到哪儿不该被继承
  }
  const generationError = await consumeGenerationStream(
    window,
    activeController?.signal,
    {
      // 推理增量：与 delta 同样是增量、同样成千上万个，同样受 seq 防串保护
      onThinking: text => { if (isVisible()) thinkingText.value += text },
      // 增量原样追加，段落由 paragraphs 对累积文本重算（不做打字机动画）
      onDelta: text => { if (isVisible()) streamText.value += text },
      onDone: data => {
        if (!isVisible()) return
        recap.data.value = data // 与 GET 同一个 DTO 形状，渲染逻辑只有一份
        streamText.value = ''
        thinkingText.value = ''
      },
      onError: message => {
        if (!isVisible()) return
        streamError.value = message
        streamText.value = '' // 半截正文不是叙事：退回上次成功的那一版
        thinkingText.value = '' // 失败后留着一屏推理只是噪音
      },
    },
  )
  if (activeController && controller === activeController) controller = null
  if (isVisible()) streaming.value = false
  return generationError
}

async function generate() {
  await runGeneration(props.calendarContext, true)
}

/**
 * 纠正提交成功后的重生成：失败必须重新抛出——纠正面板要据此区分"知识已存、Recap 未更新"。
 */
async function regenerateForCorrection(calendarContext: CalendarContext) {
  const generationError = await runGeneration(calendarContext, false)
  if (generationError) throw new Error(generationError)
}

watch(() => [props.calendarContext.correlationIdentity, props.calendarContext.day], () => {
  const nextWindow = props.calendarContext.day
  const nextIdentity = props.calendarContext.correlationIdentity
  const windowChanged = displayedWindow !== null && !sameCalendarWindow(displayedWindow, nextWindow)
  const generationChanged = displayedIdentity !== null && displayedIdentity !== nextIdentity
  displayedWindow = nextWindow
  displayedIdentity = nextIdentity
  if (generationChanged) {
    streamSeq++ // 作废在途的流：即使规范窗口相同，它也属于上一个 refresh generation
    abortStream()
    streaming.value = false
    streamText.value = ''
    thinkingText.value = ''
    streamError.value = ''
  }
  if (windowChanged) {
    recap.data.value = null // 换窗口不展示上一个窗口的旧叙事
  }
  load()
}, { immediate: true })

onUnmounted(() => {
  streamSeq++
  abortStream()
  thinkingText.value = ''
})

/** 在途的流优先显示累积文本，否则显示上次成功保存的叙事。 */
const narrative = computed(() => streamText.value || recap.data.value?.narrative || '')

const paragraphs = computed(() =>
  narrative.value.split(/\n+/).map(s => s.trim()).filter(Boolean)
)

const generatedAtStr = computed(() => {
  const at = recap.data.value?.generatedAt
  if (!at) return ''
  return new Date(at).toLocaleString('zh-CN', {
    month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit',
  })
})

const isUnavailableToVisitor = computed(() =>
  !props.canRegenerate && recap.error.value?.kind === 'http' && recap.error.value.status === 404
)

/** 读取（GET）层的失败文案。生成失败不走状态码，见 streamError（ADR-042 §4）。 */
const errorMessage = computed(() => {
  const e = recap.error.value
  if (!e) return ''
  if (e.kind === 'calendar') return e.message
  if (e.kind === 'network') return '网络连接失败，请检查网络后重试'
  if (e.kind === 'http') return `服务器返回错误（${e.status}），请稍后重试`
  return '数据解析失败，请重试'
})
</script>

<template>
  <DashboardCard v-if="!isUnavailableToVisitor">
    <div class="flex items-center justify-between gap-3">
      <h2 class="text-sm font-semibold text-foreground">这一天 · Recap</h2>
      <button
        v-if="canRegenerate && recap.data.value && !recap.data.value.isEmpty"
        class="glass-control cursor-pointer whitespace-nowrap px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground disabled:cursor-default disabled:opacity-50"
        :disabled="recap.pending.value || streaming"
        title="用最新数据重新生成这一天的回顾"
        @click="generate()"
      >重新生成</button>
    </div>

    <!-- 首次读取中（无旧数据可展示时） -->
    <div v-if="recap.pending.value && !recap.data.value" class="py-6 text-center text-[0.9rem] text-muted-foreground">
      <span class="recap-thinking">正在回忆这一天…</span>
    </div>

    <!-- 读取出错（保留上次成功的叙事时不打断阅读，只在无数据时占位） -->
    <div
      v-else-if="errorMessage && !recap.data.value"
      class="flex items-center justify-between gap-3 py-2 text-[0.85rem] text-muted-foreground"
    >
      <span>{{ errorMessage }}</span>
      <button
        class="glass-control shrink-0 cursor-pointer px-2.5 py-1 text-[0.75rem]"
        :disabled="recap.pending.value"
        @click="load()"
      >重试</button>
    </div>

    <!-- 空日 -->
    <div v-else-if="recap.data.value?.isEmpty" class="py-6 text-center text-[0.9rem] text-muted-foreground">
      这一天没有记录。
    </div>

    <!-- 生成中但还没有首个 delta：思考期可能长达数分钟，推理透传把这段沉默变成进度 -->
    <div v-else-if="streaming && !narrative" class="flex flex-col gap-3 py-4">
      <div class="text-center text-[0.9rem] text-muted-foreground">
        <span class="recap-thinking">{{ thinkingText ? '正在思考…' : '正在回忆这一天…' }}</span>
      </div>
      <!--
        推理面板：min-h == max-h 是刻意的固定高度——推理是每秒几十字符地长，
        让容器跟着内容长会把整张卡片顶得一直抖；固定一格 9rem 只在首个推理到达时占位一次。
        recap-thinking-panel 同时是滚动行为的样式与测试锚点（见 style 块）。
      -->
      <div
        v-if="thinkingText"
        ref="thinkingPanel"
        class="recap-thinking-panel min-h-[9rem] max-h-[9rem] overflow-y-auto whitespace-pre-wrap rounded-md border border-border/50 bg-muted/25 px-3 py-2 text-[0.78rem] leading-relaxed text-muted-foreground"
        @scroll="onThinkingScroll"
      >{{ thinkingText }}</div>
    </div>

    <!-- 有数据但没有叙事：从未生成，且此刻没有生成在途（生成失败后也落在这里） -->
    <div
      v-else-if="recap.data.value && !narrative"
      class="flex items-center justify-between gap-3 py-2 text-[0.85rem] text-muted-foreground"
    >
      <span>{{ streamError || '这一天还没有回顾。' }}</span>
      <button
        v-if="canRegenerate"
        class="glass-control shrink-0 cursor-pointer px-2.5 py-1 text-[0.75rem]"
        :disabled="streaming"
        @click="generate()"
      >生成</button>
    </div>

    <!-- 叙事 -->
    <template v-else-if="recap.data.value">
      <div class="flex flex-col gap-2.5 text-[0.92rem] leading-relaxed text-foreground/90">
        <p v-for="(p, i) in paragraphs" :key="i">{{ p }}</p>
      </div>
      <div class="flex items-center justify-between gap-3 text-[0.72rem] text-muted-foreground/80">
        <span v-if="generatedAtStr">生成于 {{ generatedAtStr }}<template v-if="recap.data.value.model"> · {{ recap.data.value.model }}</template></span>
        <span v-else>&nbsp;</span>
        <span v-if="streaming" class="recap-thinking">正在生成…</span>
        <span v-else-if="streamError">{{ streamError }}</span>
        <span v-else-if="errorMessage">{{ errorMessage }}</span>
      </div>

      <!-- 纠正入口：owner-only。写知识，不是散文补丁 -->
      <RecapCorrection v-if="canRegenerate" :calendar-context="calendarContext" :regenerate="regenerateForCorrection" />
    </template>
  </DashboardCard>
</template>

<style scoped>
.recap-thinking {
  animation: recap-pulse 1.6s ease-in-out infinite;
}
@keyframes recap-pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.4; }
}
.recap-thinking-panel {
  /* 滚动条出现的那一刻不要让文字横向跳一下（面板宽度撑满卡片，重排肉眼可见） */
  scrollbar-gutter: stable;
  /* 滚到底后别把滚动接力给页面：读推理时页面被带着走很上火 */
  overscroll-behavior: contain;
}
</style>
