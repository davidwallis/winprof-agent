using System.Diagnostics.Metrics;

namespace WinProfAgent;

public sealed class AgentMetrics
{
    public const string MeterName = "WinProfAgent";

    private readonly Meter _meter = new(MeterName, "1.0.0");

    public Counter<long> Samples { get; }
    public Counter<long> Windows { get; }
    public Counter<long> ProfilesPushed { get; }
    public Counter<long> PushFailures { get; }
    public Counter<long> ProfilesDropped { get; }
    public Counter<long> EventsLost { get; }
    public Counter<long> SessionRestarts { get; }
    public Histogram<double> PushDuration { get; }
    public Histogram<double> WindowProcessingDuration { get; }
    public Histogram<long> ProfileBytes { get; }

    public AgentMetrics(ProfileQueue queue)
    {
        Samples = _meter.CreateCounter<long>("winprof.cpu.samples", "{sample}", "CPU samples captured");
        Windows = _meter.CreateCounter<long>("winprof.windows", "{window}", "Profiling windows completed");
        ProfilesPushed = _meter.CreateCounter<long>("winprof.profiles.pushed", "{profile}", "Profiles pushed to Pyroscope");
        PushFailures = _meter.CreateCounter<long>("winprof.push.failures", "{failure}", "Failed push attempts");
        ProfilesDropped = _meter.CreateCounter<long>("winprof.profiles.dropped", "{profile}", "Profiles dropped after retries or queue overflow");
        EventsLost = _meter.CreateCounter<long>("winprof.etw.events_lost", "{event}", "ETW events lost by the session");
        SessionRestarts = _meter.CreateCounter<long>("winprof.etw.session_restarts", "{restart}", "ETW session recycles");
        PushDuration = _meter.CreateHistogram<double>("winprof.push.duration", "s", "Duration of a successful push");
        WindowProcessingDuration = _meter.CreateHistogram<double>("winprof.window.processing.duration", "s", "Time spent symbolizing and encoding a window");
        ProfileBytes = _meter.CreateHistogram<long>("winprof.profile.size", "By", "Encoded profile size");
        _meter.CreateObservableGauge("winprof.push.queue.length", () => queue.Count, "{profile}", "Profiles waiting to be pushed");
    }
}
