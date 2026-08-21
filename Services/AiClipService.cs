using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MovieStoreShowcase.Services;

public class AiClipService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly string _cacheFolder;
    private readonly bool _enabled;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _pollIntervalMs;
    private readonly int _pollTimeoutSeconds;
    private readonly ILogger<AiClipService> _log;

    private const string PollinationsBaseUrl = "https://image.pollinations.ai/prompt/";

    // Pollinations is free but rate-limits bursts (HTTP 429). Multiple browser
    // sessions/tabs prefetching at once can trigger this even though each one
    // only sends requests one-at-a-time on its own. A single app-wide gate that
    // also enforces a minimum gap between requests keeps us under the limit
    // regardless of how many sessions are hitting the server concurrently.
    private static readonly SemaphoreSlim _pollinationsGate = new(1, 1);
    private static DateTime _lastPollinationsRequest = DateTime.MinValue;
    private static readonly TimeSpan _minGap = TimeSpan.FromSeconds(1.5);

    public bool IsConfigured => _enabled && !string.IsNullOrWhiteSpace(_apiKey);

    public AiClipService(IConfiguration config, IWebHostEnvironment env, ILogger<AiClipService> log)
    {
        _cacheFolder = Path.Combine(env.WebRootPath, "ai-clips");
        Directory.CreateDirectory(_cacheFolder);
        _log = log;

        var section = config.GetSection("FalAi");
        _enabled = section.GetValue<bool>("Enabled");
        _apiKey = section.GetValue<string>("ApiKey") ?? "";
        _model = section.GetValue<string>("Model") ?? "fal-ai/pixverse/v5.5/text-to-video";
        _pollIntervalMs = section.GetValue<int?>("PollIntervalMs") ?? 3000;
        _pollTimeoutSeconds = section.GetValue<int?>("PollTimeoutSeconds") ?? 180;
    }

    public async Task<string?> GetOrGenerateClipAsync(string cacheKey, string prompt, int durationSeconds)
    {
        if (!IsConfigured)
        {
            _log.LogWarning("[AiClip] {Key}: skipped - FalAi not configured (Enabled/ApiKey)", cacheKey);
            return null;
        }

        var path = Path.Combine(_cacheFolder, Sanitize(cacheKey) + ".mp4");
        if (File.Exists(path) && new FileInfo(path).Length > 2048)
        {
            _log.LogInformation("[AiClip] {Key}: cache hit", cacheKey);
            return path;
        }

        if (File.Exists(path)) File.Delete(path);

        try
        {
            int snappedDuration = durationSeconds >= 7 ? 8 : 5;
            var endpoint = $"https://queue.fal.run/{_model}";

            var body = new
            {
                prompt,
                duration = snappedDuration,
                resolution = "540p",
                aspect_ratio = "16:9",
            };

            var submitBody = await SendAuthorizedPostRequestAsync(endpoint, body, cacheKey, "submit");
            if (submitBody == null) return null;

            var submitJson = JsonDocument.Parse(submitBody);
            if (!TryGetUrlProperties(submitJson, out var statusUrl, out var responseUrl, cacheKey, submitBody))
                return null;

            if (!await PollForCompletionAsync(statusUrl!, cacheKey))
                return null;

            var resultBody = await SendAuthorizedGetRequestAsync(responseUrl!, cacheKey, "result fetch");
            if (resultBody == null) return null;

            var resultJson = JsonDocument.Parse(resultBody);
            if (!resultJson.RootElement.TryGetProperty("video", out var videoEl) ||
                !videoEl.TryGetProperty("url", out var urlEl) ||
                urlEl.GetString() is not { } videoUrl)
            {
                _log.LogError("[AiClip] {Key}: result JSON has no valid video.url: {Body}", cacheKey, resultBody);
                return null;
            }

            var bytes = await _http.GetByteArrayAsync(videoUrl);
            await File.WriteAllBytesAsync(path, bytes);
            _log.LogInformation("[AiClip] {Key}: success, saved {Bytes} bytes to {Path}", cacheKey, bytes.Length, path);
            return path;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[AiClip] {Key}: threw an exception", cacheKey);
            return null;
        }
    }

    /// <summary>
    /// Downloads (and caches) one AI-generated still image for a trailer scene via
    /// Pollinations' free endpoint. <paramref name="seed"/> makes it reproducible -
    /// same seed always requests the same image. Never throws; returns null on any
    /// failure so the caller can fall back to the gradient scene. Retries a couple
    /// times with backoff on a 429 (rate limit) instead of giving up immediately.
    /// </summary>
    public async Task<string?> GetOrGenerateSceneImageAsync(string cacheKey, string prompt, int seed)
    {
        var path = Path.Combine(_cacheFolder, "img_" + Sanitize(cacheKey) + ".jpg");
        if (File.Exists(path) && new FileInfo(path).Length > 2048)
        {
            _log.LogInformation("[AiImage] {Key}: cache hit", cacheKey);
            return path;
        }
        if (File.Exists(path)) File.Delete(path);

        var url = $"{PollinationsBaseUrl}{Uri.EscapeDataString(prompt)}?width=960&height=540&seed={seed}&nologo=true&model=flux&safe=true";

        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await _pollinationsGate.WaitAsync();
            try
            {
                var wait = _minGap - (DateTime.UtcNow - _lastPollinationsRequest);
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                _lastPollinationsRequest = DateTime.UtcNow;

                _log.LogInformation("[AiImage] {Key}: requesting free scene image (attempt {Attempt}/{Max}, Pollinations, seed={Seed}) prompt=\"{Prompt}\"", cacheKey, attempt, maxAttempts, seed, prompt);

                using var res = await _http.GetAsync(url);
                var bytes = await res.Content.ReadAsByteArrayAsync();

                if ((int)res.StatusCode == 429)
                {
                    _log.LogWarning("[AiImage] {Key}: rate limited (429), attempt {Attempt}/{Max}", cacheKey, attempt, maxAttempts);
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3 * attempt)); // backoff: 3s, 6s
                        continue;
                    }
                    return null;
                }
                if (!res.IsSuccessStatusCode)
                {
                    _log.LogError("[AiImage] {Key}: request failed - HTTP {Status}", cacheKey, (int)res.StatusCode);
                    return null;
                }
                if (bytes.Length < 2048)
                {
                    _log.LogWarning("[AiImage] {Key}: response too small ({Bytes} bytes) - treating as failure", cacheKey, bytes.Length);
                    return null;
                }

                await File.WriteAllBytesAsync(path, bytes);
                _log.LogInformation("[AiImage] {Key}: success, saved {Bytes} bytes to {Path}", cacheKey, bytes.Length, path);
                return path;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[AiImage] {Key}: threw an exception", cacheKey);
                return null;
            }
            finally
            {
                _pollinationsGate.Release();
            }
        }

        return null;
    }

    private static string Sanitize(string s) =>
        new(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());

    private async Task<string?> SendAuthorizedPostRequestAsync(string endpoint, object payload, string cacheKey, string actionName)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Key", _apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        _log.LogInformation("[AiClip] {Key}: submitting request to {Endpoint}", cacheKey, endpoint);
        using var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
        {
            _log.LogError("[AiClip] {Key}: {Action} failed - HTTP {Status}: {Body}", cacheKey, actionName, (int)res.StatusCode, body);
            return null;
        }
        return body;
    }

    private async Task<string?> SendAuthorizedGetRequestAsync(string endpoint, string cacheKey, string actionName)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Key", _apiKey);
        using var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
        {
            _log.LogError("[AiClip] {Key}: {Action} failed - HTTP {Status}: {Body}", cacheKey, actionName, (int)res.StatusCode, body);
            return null;
        }
        return body;
    }

    private bool TryGetUrlProperties(JsonDocument json, out string? statusUrl, out string? responseUrl, string cacheKey, string body)
    {
        statusUrl = null;
        responseUrl = null;

        if (!json.RootElement.TryGetProperty("status_url", out var statusUrlEl) ||
            !json.RootElement.TryGetProperty("response_url", out var responseUrlEl))
        {
            _log.LogError("[AiClip] {Key}: submit response missing status_url/response_url: {Body}", cacheKey, body);
            return false;
        }

        statusUrl = statusUrlEl.GetString();
        responseUrl = responseUrlEl.GetString();

        if (statusUrl is null || responseUrl is null)
        {
            _log.LogError("[AiClip] {Key}: status_url/response_url null: {Body}", cacheKey, body);
            return false;
        }

        return true;
    }

    private async Task<bool> PollForCompletionAsync(string statusUrl, string cacheKey)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_pollTimeoutSeconds);
        string lastStatus = "UNKNOWN";

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(_pollIntervalMs);

            var statusBody = await SendAuthorizedGetRequestAsync(statusUrl, cacheKey, "status check");
            if (statusBody == null) return false;

            var statusJson = JsonDocument.Parse(statusBody);
            lastStatus = statusJson.RootElement.TryGetProperty("status", out var s) ? (s.GetString() ?? "UNKNOWN") : "UNKNOWN";
            _log.LogInformation("[AiClip] {Key}: status={Status}", cacheKey, lastStatus);

            if (lastStatus == "COMPLETED") return true;
            if (lastStatus is "ERROR" or "FAILED")
            {
                _log.LogError("[AiClip] {Key}: generation failed on fal.ai side: {Body}", cacheKey, statusBody);
                return false;
            }
        }

        _log.LogError("[AiClip] {Key}: timed out after {Timeout}s waiting for completion (last status: {Status})", cacheKey, _pollTimeoutSeconds, lastStatus);
        return false;
    }
}