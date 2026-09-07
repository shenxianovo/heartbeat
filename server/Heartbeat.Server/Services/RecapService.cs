using System.Runtime.CompilerServices;
using Heartbeat.Core.DTOs.Recaps;
using Heartbeat.Server.Calendar;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    /// <summary>流式生成的三个时限（ADR-042 §5）。做成可注入是为了测试能用毫秒级值。</summary>
    public sealed record RecapStreamTimeouts(TimeSpan Silence, TimeSpan Overall, TimeSpan Heartbeat)
    {
        /// <summary>
        /// Silence=60s：真静默才判死——推理模型思考期每秒多帧，175s 的思考不该被算成没响应。
        /// Overall=600s：8/7 实测 181s，3000+ 段的日子留 3x 余量。
        /// Heartbeat=15s：不变。
        /// </summary>
        public static readonly RecapStreamTimeouts Default =
            new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// 每日 Recap 编排（ADR-023，知识判脏随 ADR-031 §7，读写分离随 ADR-042）。
    ///
    /// 两条路径，边界是"会不会花钱"：
    /// - <see cref="GetDailyRecapAsync"/>：读缓存 + 判空 + 判脏，全部确定性、零 LLM、零写库；
    /// - <see cref="GenerateDailyRecapStreamAsync"/>：装配 digest（DigestAssembler，与发问共用）
    ///   → 流式生成 → upsert。它就是"显式重生成"，不判缓存新鲜度（判缓存属于读路径）。
    ///
    /// 三把尺现在形状一致，全部只提示、都不自动调 LLM（ADR-042 §3）：有无缓存、segment 水位
    /// （今日窗口落后 >1h）、knowledge hash。1 小时阈值留在服务端——防轮询烧 token 的护栏不能
    /// 交给前端。空日不调 LLM 不写缓存；失败不写缓存（不覆盖上次成功正文/投影）。
    /// </summary>
    public class RecapService(
        AppDbContext db,
        IRecapGenerator generator,
        DigestAssembler assembler,
        TimeProvider? clock = null,
        RecapStreamTimeouts? timeouts = null)
    {
        /// <summary>今日缓存的新鲜度护栏：水位落后超过此值才算脏（防轮询烧 token，非产品语义）。</summary>
        private static readonly TimeSpan FreshnessThreshold = TimeSpan.FromHours(1);

        private readonly AppDbContext _db = db;
        private readonly IRecapGenerator _generator = generator;
        private readonly TimeProvider _clock = clock ?? TimeProvider.System;
        private readonly RecapStreamTimeouts _timeouts = timeouts ?? RecapStreamTimeouts.Default;

        /// <summary>
        /// 认证读取：只读，永不调 LLM、永不写库（ADR-042 §2）。查库是允许的——守住的边界是
        /// "不烧 token、不写库"，而不是"不查库"，所以判空用便宜的段存在性查询，不做完整装配。
        /// </summary>
        public async Task<DailyRecapResponse> GetDailyRecapAsync(
            string ownerId, ResolvedCalendarWindow window, CancellationToken ct = default)
        {
            var cached = await _db.Recaps
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.OwnerId == ownerId && r.WindowKey == window.WindowKey.Value, ct);

            if (cached == null)
            {
                // 从未生成：先分清"这天没数据"和"这天有数据但还没写过叙事"，免得前端白发一次生成请求。
                var hasSegments = await assembler.HasSegmentsAsync(ownerId, window.Start, window.EndExclusive, ct);
                return new DailyRecapResponse { Date = window.LocalDate, IsEmpty = !hasSegments };
            }

            // 知识判脏只在认证读取时重算（确定性、零 LLM）；null hash 的旧行惰性视为可重新生成。
            var currentHash = await assembler.ComputeKnowledgeHashAsync(ownerId, window, ct);
            var segmentStale = !await IsFreshAsync(ownerId, window.Start, window.EndExclusive, cached, ct);

            return ToResponse(
                window.LocalDate, cached,
                knowledgeStale: cached.KnowledgeHash != currentHash,
                segmentStale: segmentStale);
        }

        /// <summary>
        /// 显式生成（ADR-042 §4/§6）：逐块产出 delta，末尾落库并给出 done。
        ///
        /// 取消语义分两段，这正是选迭代器而非回调的理由——它天然表达这件事：
        /// - 消费者提前 break（客户端断开）→ 落库那一步永不执行，缓存保持上次成功的正文；
        /// - 最后一块到手后立刻用独立 CancellationToken 落库，不透传请求的 ct——钱已经花完了就
        ///   把货存下来，而不是用户手快就烧空气。
        ///
        /// 时限是"上游静默"而不是"首个正文 token"（ADR-042 §9）：推理模型可以先吐三分钟
        /// reasoning 再落笔，那期间每秒好几帧，它活得很好。只有真的一帧都不来才判死。
        ///
        /// 生成域的失败以 error 事件在流内抵达（头已发出，502 不再可能），且不写缓存。
        /// </summary>
        public async IAsyncEnumerable<RecapStreamEvent> GenerateDailyRecapStreamAsync(
            string ownerId, ResolvedCalendarWindow window, [EnumeratorCancellation] CancellationToken ct = default)
        {
            RecapProjectionResult projection;
            try
            {
                projection = await assembler.AssembleAsync(ownerId, window, ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (projection.IsEmpty)
            {
                // 空日不调 LLM 不写缓存（ADR-023 §5）：直接以 done 告知空态。
                yield return RecapStreamEvent.OfDone(new DailyRecapResponse { Date = window.LocalDate, IsEmpty = true });
                yield break;
            }

            var cached = await _db.Recaps
                .FirstOrDefaultAsync(r => r.OwnerId == ownerId && r.WindowKey == window.WindowKey.Value, ct);

            var narrative = new System.Text.StringBuilder();

            // 两层 CTS 表达两个独立的时限，嵌套关系就是它们的语义关系：整段上限是天花板，
            // 静默判死线活在它下面并且每收到一块就重新计时。判死的时候靠"谁被取消了"区分文案。
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            overall.CancelAfter(_timeouts.Overall);
            using var silence = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            silence.CancelAfter(_timeouts.Silence);

            var enumerator = _generator.GenerateStreamAsync(projection.Digest, silence.Token)
                .GetAsyncEnumerator(silence.Token);

            // 这次取块的 Task 活在循环外：心跳只是"这一轮还没等到块"，不是一次新的取块。同一个
            // MoveNextAsync 只能被 await 一次，重新 MoveNext 会丢掉在途的那一块，甚至撞上迭代器的
            // "上一次还在进行中"。
            Task<bool>? pending = null;
            try
            {
                while (true)
                {
                    pending ??= enumerator.MoveNextAsync().AsTask();
                    var step = await NextChunkAsync(pending, enumerator, silence, overall.Token, ct, _timeouts);

                    if (step.Heartbeat)
                    {
                        yield return RecapStreamEvent.OfPing();
                        continue; // pending 原样留着，下一轮继续等它
                    }

                    if (step.Failure != null)
                    {
                        yield return RecapStreamEvent.OfError(step.Failure);
                        yield break;
                    }

                    if (step.ClientGone) yield break;

                    // 取消可能先于上游 MoveNext 的清理结束；终态分支必须把 pending 留给 finally。
                    // 只有实际取得一块或读到流末尾，才能释放这次读取的所有权。
                    pending = null;
                    if (step.Completed) break;

                    // 收到任何一块（含只有思考的块）都证明上游活着：静默判死线整段重置。
                    silence.CancelAfter(_timeouts.Silence);

                    if (step.Kind == LlmChunkKind.Reasoning)
                    {
                        // 推理增量只透传给前端显示"正在思考"，不进叙事——它不是要落库的正文。
                        yield return RecapStreamEvent.OfThinking(step.Text!);
                        continue;
                    }

                    narrative.Append(step.Text);
                    yield return RecapStreamEvent.OfDelta(step.Text!);
                }
            }
            finally
            {
                // 消费者可能正好在心跳那次 yield 上 break，此时上游还挂着一次 MoveNextAsync；对同一个
                // async iterator 并发 MoveNext 与 DisposeAsync 是未定义行为，先取消让它落地再 Dispose。
                if (pending != null)
                {
                    await silence.CancelAsync();
                    try
                    {
                        await pending;
                    }
                    catch (Exception)
                    {
                        // 这次生成已作废（连接没了），失败原因无处可去。
                    }
                }
                await enumerator.DisposeAsync();
            }

            if (narrative.Length == 0)
            {
                yield return RecapStreamEvent.OfError("LLM 未产出任何叙事内容。");
                yield break;
            }

            var response = await PersistAsync(ownerId, window, cached, narrative.ToString(), projection);
            yield return RecapStreamEvent.OfDone(response);
        }

        /// <summary>
        /// 等一块，同时兼顾心跳与两个时限。yield 不能出现在带 catch 的 try 里，所以这里把
        /// "这一步发生了什么"收敛成一个结果，由调用方在 try 之外 yield。取块的 Task 由调用方持有
        /// 并跨心跳复用，本方法只负责等它。
        /// </summary>
        private static async Task<ChunkStep> NextChunkAsync(
            Task<bool> pending, IAsyncEnumerator<LlmChunk> enumerator,
            CancellationTokenSource silence, CancellationToken overallToken,
            CancellationToken clientToken, RecapStreamTimeouts timeouts)
        {
            // 心跳计时器挂在自己的 CTS 上：这一轮一结束就取消，否则每等一块都漏一个 15s timer。
            // 同时链上 silence——时限到了要立刻醒来，不能再干等一个心跳周期。
            using var beat = CancellationTokenSource.CreateLinkedTokenSource(silence.Token);
            try
            {
                // 块已经到手就不进竞速：让"最后一块之后请求才被取消"这条路径不受调度顺序摆布——
                // 已经花掉的钱要落库（ADR-042 §6），不能被判成客户端断开。
                if (!pending.IsCompleted)
                {
                    var heartbeat = Task.Delay(timeouts.Heartbeat, beat.Token);
                    if (await Task.WhenAny(pending, heartbeat) != pending)
                    {
                        // 时限一到 Task.Delay 会因 linked token 立刻返回，这里必须自己判死：否则
                        // 心跳退化成忙循环，判死被拖成一条永不结束的流。
                        silence.Token.ThrowIfCancellationRequested();
                        return ChunkStep.Ping;
                    }
                }

                return await pending ? ChunkStep.Of(enumerator.Current) : ChunkStep.Done;
            }
            catch (RecapGenerationException ex)
            {
                return ChunkStep.Failed(ex.Message);
            }
            catch (OperationCanceledException)
            {
                // 三种取消长得一样，靠"谁被取消了"分辨，顺序即优先级：客户端断开时 overall 也已被
                // 连带取消（它 link 在 ct 上），先判客户端才不会把断开写成一句超时。
                if (clientToken.IsCancellationRequested) return ChunkStep.Disconnected;

                if (overallToken.IsCancellationRequested)
                    return ChunkStep.Failed(
                        $"生成超时：LLM 超过 {Describe(timeouts.Overall)}仍未产出完整叙事。");

                return ChunkStep.Failed($"生成中断：LLM 连续 {Describe(timeouts.Silence)}没有任何响应。");
            }
            finally
            {
                beat.Cancel();
            }
        }

        /// <summary>时限的可读写法：文案里的数字必须由注入值算出来，否则改了时限文案就开始说谎。</summary>
        private static string Describe(TimeSpan span) =>
            span.TotalSeconds >= 60 && span.TotalSeconds % 60 == 0
                ? $"{span.TotalMinutes:0.###} 分钟"
                : $"{span.TotalSeconds:0.###} 秒";

        private async Task<DailyRecapResponse> PersistAsync(
            string ownerId, ResolvedCalendarWindow window,
            Recap? cached, string narrative, RecapProjectionResult projection)
        {
            if (cached == null)
            {
                cached = new Recap { OwnerId = ownerId, WindowKey = window.WindowKey.Value };
                _db.Recaps.Add(cached);
            }
            cached.WindowVersion = window.Version;
            cached.WindowKind = window.Kind;
            cached.LocalDate = window.LocalDate;
            cached.TimeZone = window.TimeZone;
            cached.WindowStart = window.Start;
            cached.WindowEndExclusive = window.EndExclusive;
            cached.Narrative = narrative;
            cached.GeneratedAt = _clock.GetUtcNow();
            cached.Model = _generator.Model;
            cached.PromptHash = _generator.PromptHash;
            cached.SegmentWatermark = projection.SegmentWatermarkUtc;
            cached.KnowledgeHash = projection.KnowledgeHash;

            // 落库不受客户端取消影响（ADR-042 §6）：已完整生成的叙事不该因为用户手快而蒸发。
            await _db.SaveChangesAsync(CancellationToken.None);

            return ToResponse(window.LocalDate, cached, knowledgeStale: false, segmentStale: false);
        }

        /// <summary>
        /// 公开视角只读已有缓存：不查询段、不读取私有知识、不重算投影、不调用生成器。
        /// 未生成过的日期返回 null，由公开端点映射为 404，前端不渲染卡片。
        /// </summary>
        public async Task<DailyRecapResponse?> GetCachedDailyRecapAsync(
            string ownerId, ResolvedCalendarWindow window, CancellationToken ct = default)
        {
            var cached = await _db.Recaps
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.OwnerId == ownerId && r.WindowKey == window.WindowKey.Value, ct);

            // 两个判脏位恒 false：判脏需要私有知识与段数据，公开路径不暴露投影细节。
            return cached == null ? null : ToResponse(window.LocalDate, cached, knowledgeStale: false, segmentStale: false);
        }

        private async Task<bool> IsFreshAsync(
            string ownerId, DateTimeOffset windowStart, DateTimeOffset windowEnd, Recap cached, CancellationToken ct)
        {
            // 已结束的窗口是历史：段层面永不过期（离线重传的迟到段由用户显式重生成收敛）。
            if (_clock.GetUtcNow() >= windowEnd) return true;

            var latestEnd = await assembler.LatestSegmentEndAsync(ownerId, windowStart, windowEnd, ct);
            return latestEnd - cached.SegmentWatermark <= FreshnessThreshold;
        }

        private static DailyRecapResponse ToResponse(
            string localDate, Recap recap, bool knowledgeStale, bool segmentStale) => new()
            {
                Date = localDate,
                IsEmpty = false,
                Narrative = recap.Narrative,
                GeneratedAt = recap.GeneratedAt,
                Model = recap.Model,
                KnowledgeStale = knowledgeStale,
                SegmentStale = segmentStale
            };

        /// <summary>
        /// 一次取块的结果：心跳 / 一块（带分型的）文本 / 正常结束 / 客户端断开 / 可读失败。
        /// Kind 只在拿到块时有值——"这一步到底发生了什么"必须一眼可辨，别用 Text 是否为 null 兼职。
        /// </summary>
        private readonly record struct ChunkStep(
            bool Heartbeat, LlmChunkKind? Kind, string? Text, bool Completed, bool ClientGone, string? Failure)
        {
            public static readonly ChunkStep Ping = new(true, null, null, false, false, null);
            public static readonly ChunkStep Done = new(false, null, null, true, false, null);
            public static readonly ChunkStep Disconnected = new(false, null, null, false, true, null);

            public static ChunkStep Of(LlmChunk chunk) => new(false, chunk.Kind, chunk.Text, false, false, null);

            public static ChunkStep Failed(string message) => new(false, null, null, false, false, message);
        }
    }
}
