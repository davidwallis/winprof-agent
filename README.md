# winprof-agent

A Windows service that continuously profiles processes using ETW and pushes pprof profiles to Grafana Pyroscope.

Targets: C++, VB6 (native-compiled), .NET Framework, .NET (Core), including COM and COM+ hosts.

## Design

- .NET 10 Worker Service, runs as SYSTEM (needs `SeSystemProfilePrivilege`).
- Direct real-time ETW session via TraceEvent (kernel sampled profile with stacks, image load, CLR rundown). Does not shell out to `wpr.exe`.
- Rolling windows (`Agent:WindowSeconds`); each window is converted to one pprof profile per process and pushed to Pyroscope.
- Symbolization runs on a background thread (`SymbolWorker`); the ETW event thread only snapshots each window.
- PDB lookup order: beside the binary, `Symbols:PdbSearchPaths`, then `Symbols:SymbolFolder` (default `C:\symbols`, also the download target). With `AllowDownload: true`, missing PDBs are fetched from `Symbols:SymbolServers` in order of preference, only for processes matching `Processes:Targets` (an empty Targets list disables downloads).
- Processes are chosen with `Processes:Targets`, each `{ "Name": "<service_name>", "Process": "<image name>" }`; `Name` is the Pyroscope service name (defaults to the process name). An empty list profiles every process under its own name. `Processes:Exclude` always wins.
- A missing PDB is logged once per module (retried after `MissingRetryMinutes`) and counted in `winprof.symbols.missing`.
- Configured through `appsettings.json` (plus environment variables).

## Profile types

- `process_cpu` (all runtimes): kernel sampling; interval is measured from the data.
- `memory:alloc_objects` / `memory:alloc_space` (.NET only): CLR allocation ticks (~every 100 KB), with the allocated type as the leaf frame. Disable with `Agent:CollectAllocations=false`.
- `mutex:contentions` / `mutex:delay` (.NET only): CLR lock contention. Disable with Agent:CollectContentions=false.
- disk_bytes / 
etwork_bytes (all runtimes): file read/write and TCP/UDP send/receive, each with a disk_operations / 
etwork_operations count. Stacks end in a ead/write/	cp send... leaf frame. Disable with Agent:CollectDisk=false / Agent:CollectNetwork=false.
- wait_time (off-CPU, opt-in with Agent:CollectWaits=true): time threads spend blocked, from context switches. High event volume, so off by default.

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

## Test apps

CPU-burning targets for verifying the profiler (add the process to `Agent:Processes:Targets`):

- `test-apps\busy-net10`: .NET 10 (`dotnet build -c Release`, process `busy`).
- `test-apps\busy-netfx`: .NET Framework 4.x (`.\build.ps1`, output in `out\`, process `FxBusy`).
- `test-apps\busy-native`: native C++ (open `BusyNative.vcxproj` in Visual Studio, or run `.\build.ps1`; output and PDB in `out\`, process `BusyNative`). Use this to check PDB-based symbolization of native frames.
- `test-apps\busy-com`: ATL in-proc COM DLL hosted in a `dllhost.exe` surrogate. Run `.\build.ps1`, then `.\register.ps1` (HKCU, no admin; `-Unregister` removes it), then start `out\BusyComClient.exe`. Profiles appear under the `com-demo` target (`dllhost.exe`) with `CBusy::Compute` and its callees resolved from the DLL's PDB. A target may set `CommandLineContains` (e.g. the AppID GUID in `dllhost.exe /Processid:{GUID}`) so each COM+ application gets its own service name.

Run `test-apps\launch.ps1` to build (if needed) and start all of them; `-Stop` stops them, `-Rebuild` forces rebuilds. The default `Targets` in `appsettings.json` cover all three apps. The .NET apps also allocate and contend on a lock, to exercise the allocation and contention profiles.

## Status

Capture, symbolization (TraceLog + PDBs), pprof encoding and Pyroscope push are implemented. The pprof encoder is validated with `go tool pprof`. Managed, native and kernel frames are verified end to end against Pyroscope. Native C++ and an ATL COM server in a dllhost surrogate are verified. Running as a Windows service and OTLP metrics export (service name `winprof-agent`, checked against an OpenTelemetry Collector) are verified. Still untested: VB6 and full COM+ applications.
