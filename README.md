# winprof-agent

A Windows service that continuously profiles processes using ETW and pushes pprof profiles to Grafana Pyroscope.

Targets: C++, VB6 (native-compiled), .NET Framework, .NET (Core), including COM and COM+ hosts.

## Design

- .NET 10 Worker Service, runs as SYSTEM (needs `SeSystemProfilePrivilege`).
- Direct real-time ETW session via TraceEvent (kernel sampled profile with stacks, image load, CLR rundown). Does not shell out to `wpr.exe`.
- Rolling windows (`Agent:WindowSeconds`); each window is converted to one pprof profile per process and pushed to Pyroscope.
- Symbols from PDBs in `Agent:Symbols:PdbSearchPaths`. Downloading is off by default (`Symbols:AllowDownload: false`): only local folders are used, and `SRV*cache*url` entries are reduced to their local cache folder. Set `AllowDownload: true` to fetch from a symbol server.
- Configured through `appsettings.json` (plus environment variables).

## Known limits

- VB6 p-code shows only `msvbvm60` frames; native-compiled VB6 with PDBs resolves normally.
- x86 stacks without frame pointers may be incomplete.

## Metrics

OpenTelemetry metrics (OTLP) from the `WinProfAgent` meter plus .NET runtime instrumentation. Configure with `Agent:Metrics` or the standard `OTEL_EXPORTER_OTLP_*` environment variables. No tracing.

Key instruments: `winprof.cpu.samples`, `winprof.windows`, `winprof.profiles.pushed`, `winprof.push.failures`, `winprof.profiles.dropped`, `winprof.etw.events_lost`, `winprof.push.queue.length`, `winprof.push.duration`, `winprof.window.processing.duration`, `winprof.profile.size`.

## Install

```powershell
dotnet publish src\WinProfAgent -c Release -r win-x64 --self-contained -o publish
sc.exe create WinProfAgent binPath= "C:\path\publish\WinProfAgent.exe" start= auto
sc.exe start WinProfAgent
```

For a console run, use an elevated shell (`dotnet run --project src\WinProfAgent`).

## Status

Capture, symbolization (TraceLog + PDBs), pprof encoding and Pyroscope push are implemented. The pprof encoder is validated with `go tool pprof`. The live ETW path needs testing on an elevated machine, especially native module symbolization for processes that started before the service (real-time sessions don't receive image-ID rundown events).
