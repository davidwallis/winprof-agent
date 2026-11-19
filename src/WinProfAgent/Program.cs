using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using WinProfAgent;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "WinProfAgent");
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));

builder.Services.AddSingleton<ProfileQueue>();
builder.Services.AddSingleton<AgentMetrics>();
builder.Services.AddSingleton<SnapshotQueue>();
builder.Services.AddHttpClient<PyroscopeClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHostedService<PushWorker>();
builder.Services.AddHostedService<SymbolWorker>();
builder.Services.AddHostedService<EtwCollector>();

var metricsOptions = builder.Configuration.GetSection("Agent:Metrics").Get<MetricsOptions>() ?? new MetricsOptions();
if (metricsOptions.Enabled)
{
    builder.Services.AddOpenTelemetry().WithMetrics(m =>
    {
        m.ConfigureResource(r => r.AddService("winprof-agent")).AddMeter(AgentMetrics.MeterName).AddRuntimeInstrumentation();
        m.AddOtlpExporter((exporter, reader) =>
        {
            if (!string.IsNullOrEmpty(metricsOptions.OtlpEndpoint)) exporter.Endpoint = new Uri(metricsOptions.OtlpEndpoint);
            reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = metricsOptions.ExportIntervalSeconds * 1000;
        });
    });
}

var host = builder.Build();
host.Run();
