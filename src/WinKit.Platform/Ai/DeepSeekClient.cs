using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinKit.Core.Ai;
using WinKit.Core.Diagnostics;
using WinKit.Core.Serialization;

namespace WinKit.Platform.Ai
{
    /// <summary>
    /// <b>全项目唯一的网络出口。</b>
    ///
    /// 这个事实由机器守住：<c>scripts\check-conventions.ps1</c> 里有一条规则 ——
    /// 除本文件外，任何 src 下的文件出现 HttpClient / WebRequest / WebClient 一律报错。
    /// 把"只有一个出口"变成可校验的约束，比在文档里承诺可靠得多。
    ///
    /// 另外：本类**只在被显式调用时**才发起请求。应用启动、页面加载、体检扫描都不会碰网络。
    /// </summary>
    public sealed class DeepSeekClient : IDisposable
    {
        private const string DefaultBaseUrl = "https://api.deepseek.com";

        private readonly HttpClient _http;
        private readonly bool _ownsHttpClient;

        public DeepSeekClient(string apiKey, string? baseUrl = null, HttpClient? httpClient = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API Key 不能为空", nameof(apiKey));

            ApiKey = apiKey.Trim();
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl!.TrimEnd('/');

            if (httpClient != null)
            {
                _http = httpClient;
                _ownsHttpClient = false;
            }
            else
            {
                // net48 默认 SystemDefault：Win10 19045 上即 TLS 1.2/1.3，无需手工提升
                _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                _ownsHttpClient = true;
            }

            _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        }

        public string ApiKey { get; }
        public string BaseUrl { get; }

        /// <summary>非流式调用：一次拿回完整回复。</summary>
        public async Task<string> CompleteAsync(AiRequest request, CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            var builder = new StringBuilder();
            await StreamAsync(request, chunk => builder.Append(chunk), cancellationToken, timeout).ConfigureAwait(false);
            return builder.ToString();
        }

        /// <summary>
        /// 流式调用。**net48 没有 IAsyncEnumerable**（那是 netstandard2.1 的东西），
        /// 所以这里用回调把增量推给调用方 —— 界面侧再封送到 UI 线程。
        /// 顺便：非流式调用也是复用这个方法的实现，避免两套解析逻辑。
        /// </summary>
        public async Task StreamAsync(AiRequest request, Action<string> onDelta, CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (onDelta == null) throw new ArgumentNullException(nameof(onDelta));

            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(120);

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                linked.CancelAfter(effectiveTimeout);

                HttpResponseMessage? response = null;
                try
                {
                    var payload = BuildRequestBody(request);

                    using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                    using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/chat/completions"))
                    {
                        httpRequest.Content = content;

                        response = await _http
                            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                            .ConfigureAwait(false);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw await BuildHttpErrorAsync(response, linked.Token).ConfigureAwait(false);
                    }

                    await ReadStreamAsync(response, request.Stream, onDelta, linked.Token).ConfigureAwait(false);
                }
                catch (AiException)
                {
                    throw;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new AiException(AiErrorKind.Cancelled, "用户取消了请求");
                }
                catch (OperationCanceledException ex)
                {
                    throw new AiException(AiErrorKind.NetworkUnavailable, "请求超时（" + (int)effectiveTimeout.TotalSeconds + " 秒）", ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new AiException(AiErrorKind.NetworkUnavailable, "网络请求失败：" + ex.Message, ex);
                }
                finally
                {
                    if (response != null) response.Dispose();
                }
            }
        }

        private static string BuildRequestBody(AiRequest request)
        {
            // 流式标记由请求对象决定；这里复制一份避免改动调用方对象
            var payload = new AiRequest
            {
                Model = request.Model,
                Messages = request.Messages,
                Temperature = request.Temperature,
                MaxTokens = request.MaxTokens,
                Stream = request.Stream,
            };
            return Json.Serialize(payload);
        }

        private static async Task<AiException> BuildHttpErrorAsync(HttpResponseMessage response, CancellationToken token)
        {
            var status = (int)response.StatusCode;
            string body = string.Empty;
            try { body = await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
            catch (Exception) { }

            // 只记前 200 字符，且它通常只含错误码与提示，不含密钥
            var detail = body == null ? string.Empty : body.Trim();
            if (detail.Length > 200) detail = detail.Substring(0, 200) + "…";

            switch (status)
            {
                case 401:
                case 403:
                    return new AiException(AiErrorKind.Unauthorized, "鉴权失败（HTTP " + status + "）" + detail);
                case 402:
                case 429:
                    return new AiException(AiErrorKind.RateLimited, "限流或余额不足（HTTP " + status + "）" + detail);
                default:
                    if (status >= 500)
                    {
                        return new AiException(AiErrorKind.ServerError, "服务端错误（HTTP " + status + "）" + detail);
                    }
                    return new AiException(AiErrorKind.ServerError, "请求被拒绝（HTTP " + status + "）" + detail);
            }
        }

        private static async Task ReadStreamAsync(HttpResponseMessage response, bool streaming, Action<string> onDelta, CancellationToken token)
        {
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                if (!streaming)
                {
                    var text = await reader.ReadToEndAsync().ConfigureAwait(false);
                    var content = ExtractMessageContent(text);
                    if (content.Length > 0) onDelta(content);
                    return;
                }

                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                    var data = line.Substring(5).Trim();
                    if (data.Length == 0) continue;
                    if (string.Equals(data, "[DONE]", StringComparison.Ordinal)) break;

                    var delta = ExtractDeltaContent(data);
                    if (delta.Length > 0) onDelta(delta);
                }
            }
        }

        // ---------------- SSE / JSON 解析 ----------------
        // 用 DataContract 而不是手工字符串裁剪：字段缺失与未知字段都能安全处理。

        [DataContract]
        private sealed class ChunkEnvelope
        {
            [DataMember(Name = "choices")] public List<ChunkChoice>? Choices { get; set; }
        }

        [DataContract]
        private sealed class ChunkChoice
        {
            [DataMember(Name = "delta")] public ChunkContent? Delta { get; set; }
            [DataMember(Name = "message")] public ChunkContent? Message { get; set; }
        }

        [DataContract]
        private sealed class ChunkContent
        {
            [DataMember(Name = "content")] public string? Content { get; set; }
        }

        /// <summary>从流式片段里取增量文本；解析失败按"没有内容"处理，绝不因为一行坏数据中断整段回复。</summary>
        private static string ExtractDeltaContent(string json)
        {
            var envelope = Json.TryDeserialize<ChunkEnvelope>(json);
            if (envelope?.Choices == null) return string.Empty;

            var sb = new StringBuilder();
            foreach (var choice in envelope.Choices)
            {
                if (choice.Delta?.Content != null) sb.Append(choice.Delta.Content);
            }
            return sb.ToString();
        }

        /// <summary>从非流式响应里取正文。</summary>
        private static string ExtractMessageContent(string json)
        {
            var envelope = Json.TryDeserialize<ChunkEnvelope>(json);
            if (envelope?.Choices == null) return string.Empty;

            var sb = new StringBuilder();
            foreach (var choice in envelope.Choices)
            {
                if (choice.Message?.Content != null) sb.Append(choice.Message.Content);
                else if (choice.Delta?.Content != null) sb.Append(choice.Delta.Content);
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            if (_ownsHttpClient) _http.Dispose();
        }
    }
}
