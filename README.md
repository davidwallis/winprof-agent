# winprof-agent

A Windows service that continuously profiles processes using ETW and pushes pprof profiles to Grafana Pyroscope.

Targets: C++, VB6 (native-compiled), .NET Framework, .NET (Core), including COM and COM+ hosts.

## Design

- .NET 10 Worker Service, runs as SYSTEM (needs `SeSystemProfilePrivilege`).
- Direct real-time ETW session via TraceEvent (kernel sampled profile with stacks, image load, CLR rundown). Does not shell out to `wpr.exe`.
- Rolling windows (`Agent:WindowSeconds`); each window is converted to one pprof profile per process and pushed to Pyroscope.
- Symbolization runs on a background thread (`SymbolWorker`); the ETW event thread only snapshots each window.
- PDB lookup order: beside the binary, `Symbols:PdbSearchPaths`, then `Symbols:SymbolFolder` (default `C:\symbols`, also the download target). With `AllowDownload: true`, missing PDBs are fetched from `Symbols:SymbolServers` in order of preference, only for processes in `Processes:Include` (an empty Include disables downloads).
- A missing PDB is logged once per module (retried after `MissingRetryMinutes`) and counted in `winprof.symbols.missing`.
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

## Signing (Smart App Control / WDAC)

Machines enforcing Smart App Control or WDAC may block unsigned builds. The `SignOutput` target signs `WinProfAgent.dll/.exe` after each build when `SignCertThumbprint` is set. Put it in the git-ignored `src\WinProfAgent\WinProfAgent.csproj.user`:

```xml
<Project><PropertyGroup><SignCertThumbprint>YOUR_THUMBPRINT</SignCertThumbprint></PropertyGroup></Project>
```

Create a dev cert with `New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=winprof-agent dev" -CertStoreLocation Cert:\CurrentUser\My`. For production use your organization's code-signing certificate.

## Test apps

CPU-burning targets for verifying the profiler (set `Agent:Processes:Include` to the process name):

- `test-apps\busy-net10`: .NET 10 (`dotnet build -c Release`, process `busy`).
- `test-apps\busy-netfx`: .NET Framework 4.x (`.\build.ps1`, output in `out\`, process `FxBusy`).

## Status

Capture, symbolization (TraceLog + PDBs), pprof encoding and Pyroscope push are implemented. The pprof encoder is validated with `go tool pprof`. Managed, native and kernel frames are verified end to end against Pyroscope. Still untested: VB6, native C++ and COM+ targets, running as a service, and OTLP export.
