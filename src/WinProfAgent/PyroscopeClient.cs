using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;

namespace WinProfAgent;

public sealed class PyroscopeClient(HttpClient http, IOptions<AgentOptions> options)
{
    private readonly PyroscopeOptions _opt = options.Value.Pyroscope;

    public async Task PushAsync(ProfilePayload p, CancellationToken ct)
    {
        var uri = new Uri(new Uri(_opt.Url.TrimEnd('/') + "/"), "ingest?" +
            $"name={Uri.EscapeDataString(BuildName(p))}" +
            $"&from={p.From.ToUnixTimeSeconds()}&until={p.Until.ToUnixTimeSeconds()}");

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(p.Pprof);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "profile", "profile.pprof");

        using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = form };
        if (_opt.BasicAuthUser is { Length: > 0 })
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_opt.BasicAuthUser}:{_opt.BasicAuthPassword}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        if (_opt.TenantId is { Length: > 0 }) req.Headers.Add("X-Scope-OrgID", _opt.TenantId);

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Pyroscope returned {(int)resp.StatusCode}: {body}", null, resp.StatusCode);
        }
    }

    // Pyroscope app name format: app{label=value,...}; service names allow [A-Za-z0-9._-] only
    private static string BuildName(ProfilePayload p)
    {
        var sb = new StringBuilder(Sanitize(p.AppName)).Append('{');
        var first = true;
        foreach (var (k, v) in p.Labels)
        {
            if (!first) sb.Append(',');
            sb.Append(Sanitize(k)).Append('=').Append(v.Replace(',', '_').Replace('}', '_').Replace('{', '_'));
            first = false;
        }
        return sb.Append('}').ToString();
    }

    private static string Sanitize(string s) =>
        string.Create(s.Length, s, (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_';
            }
        });
}

public sealed class PushWorker(
    ProfileQueue queue,
    PyroscopeClient client,
    AgentMetrics metrics,
    IOptions<AgentOptions> options,
    ILogger<PushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        queue.Dropped += () => metrics.ProfilesDropped.Add(1);
        var attempts = Math.Max(1, options.Value.Pyroscope.MaxAttempts);

        try
        {
            await foreach (var p in queue.ReadAllAsync(stoppingToken))
            {
                for (var attempt = 1; attempt <= attempts; attempt++)
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        await client.PushAsync(p, stoppingToken);
                        metrics.PushDuration.Record(sw.Elapsed.TotalSeconds);
                        metrics.ProfilesPushed.Add(1);
                        break;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        metrics.PushFailures.Add(1);
                        logger.LogWarning(ex, "Push of {App} failed (attempt {Attempt}/{Max})", p.AppName, attempt, attempts);
                        // Client errors (e.g. a profile outside the ingestion window) will never succeed on retry.
                        var permanent = ex is HttpRequestException { StatusCode: { } sc } && (int)sc is >= 400 and < 500 && (int)sc != 429 && (int)sc != 408;
                        if (permanent || attempt == attempts)
                        {
                            metrics.ProfilesDropped.Add(1);
                            break;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), stoppingToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
