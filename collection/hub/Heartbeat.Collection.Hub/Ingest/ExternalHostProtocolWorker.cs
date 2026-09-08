using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.Net;
using Heartbeat.Collection.Hub.Hosting;
using Heartbeat.Collection.Hub.Upload;

namespace Heartbeat.Collection.Hub.Ingest
{
    /// <summary>
    /// ExternalHost Collector Protocol 的 loopback HTTP server。仅负责监听器生命周期；路由、协议
    /// 协商与 Fact 交付由注册进来的 binding handler 拥有。宿主用
    /// <c>AddExternalHostCollectorBinding</c> 接入通用 binding；没接入的宿主保留默认 adapter，
    /// 一律 404。两种情况下监听器都不认识任何具名 Collector（ADR-049）。
    /// </summary>
    public class ExternalHostProtocolWorker(
        IExternalHostProtocolHttpHandler handler,
        IHubConfiguration configuration) : BackgroundService, IHostShutdownEvidence
    {
        // Only our listener/in-flight handler belongs to this process; external outboxes do not.
        private bool _stopped;
        public DeliveryRemainder ShutdownRemainder => _stopped ? new(0, 0) : DeliveryRemainder.Unknown;

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(CancellationToken.None);
            _stopped = true;
        }

        /// <summary>
        /// 端口浮动范围：基准端口被占时向上顺延试绑的端口数。
        /// ExternalHost 侧按同一范围探测 discovery endpoint，两侧约定一致。
        /// </summary>
        public const int PortRange = 10;

        /// <summary>范围内全部被占时的重试间隔：占用者（如未退净的旧实例）通常短时间内让位。</summary>
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var basePort = configuration.Current.IngestPort;
            if (basePort <= 0)
            {
                Log.Information("本地 ingest 枢纽未启用（ingestPort = {Port}）", basePort);
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                using var listener = TryStartListener(basePort, out var port);
                if (listener == null)
                {
                    Log.Warning(
                        "本地 ingest 枢纽启动失败（端口 {BasePort}–{EndPort} 均被占用），{Retry} 秒后重试",
                        basePort, basePort + PortRange - 1, RetryInterval.TotalSeconds);
                    try
                    {
                        await Task.Delay(RetryInterval, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    continue;
                }

                Log.Information("本地 ingest 枢纽已启动: http://127.0.0.1:{Port}/", port);
                await ServeAsync(listener, stoppingToken);
                Log.Information("本地 ingest 枢纽已停止");
                return;
            }
        }

        /// <summary>从基准端口起顺延试绑 <see cref="PortRange"/> 个端口，全占返回 null。</summary>
        private static HttpListener? TryStartListener(int basePort, out int boundPort)
        {
            for (var port = basePort; port < basePort + PortRange && port <= 65535; port++)
            {
                var listener = new HttpListener();
                // loopback 限定：非本机流量到不了这个前缀。
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    boundPort = port;
                    return listener;
                }
                catch (HttpListenerException ex)
                {
                    listener.Close();
                    Log.Debug(ex, "端口 {Port} 被占用，尝试下一个", port);
                }
            }
            boundPort = 0;
            return null;
        }

        private async Task ServeAsync(HttpListener listener, CancellationToken stoppingToken)
        {
            using var _ = stoppingToken.Register(listener.Stop);

            while (!stoppingToken.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch (Exception) when (stoppingToken.IsCancellationRequested)
                {
                    break; // Stop() 会让 GetContextAsync 抛出
                }

                // 逐请求串行处理即可：本机插件低频小批量，无并发压力。
                try
                {
                    await HandleRequestAsync(ctx);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "ingest 请求处理异常");
                    TryRespond(ctx, 500, "internal error");
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var response = await handler.HandleAsync(req.HttpMethod, req.Url?.AbsolutePath, req.InputStream);
            if (response is null)
            {
                TryRespond(ctx, 404, "not found");
                return;
            }
            TryRespond(ctx, response.StatusCode, response.Body, response.IsJson);
        }

        private static void TryRespond(HttpListenerContext ctx, int status, string body, bool json = false)
        {
            try
            {
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = json ? "application/json" : "text/plain";
                using var writer = new StreamWriter(ctx.Response.OutputStream);
                writer.Write(body);
            }
            catch
            {
                // 客户端可能已断开；忽略。
            }
        }
    }
}
