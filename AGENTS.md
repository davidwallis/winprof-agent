# AGENTS.md

Guidance for AI coding agents working in this repo.

## What this is

`winprof-agent`: a Windows service (.NET 10 Worker Service, TraceEvent) that samples CPU via ETW, converts to pprof and pushes to Grafana Pyroscope (`/ingest`). It also exports OpenTelemetry metrics (no traces).

## Layout

- `src\WinProfAgent\EtwCollector.cs`: ETW session. The event thread only counts samples and snapshots each window. Keep it fast; never symbolize here.
- `src\WinProfAgent\SymbolWorker.cs`: background symbolization, PDB lookup/download, pprof building.
- `src\WinProfAgent\WindowSnapshot.cs`: data contract between the two, plus `SnapshotQueue`.
- `src\WinProfAgent\Pprof\PprofBuilder.cs`: hand-written pprof protobuf encoder (no gzip, Pyroscope accepts raw).
- `src\WinProfAgent\PyroscopeClient.cs`, `ProfileQueue.cs`: push with retry.
- `src\WinProfAgent\AgentOptions.cs`, `appsettings.json`: all configuration (`Agent` section).
- `test-apps\`: CPU-burning targets (`busy-net10`, `busy-netfx`).

## Build

```
dotnet build WinProfAgent.slnx
test-apps\busy-netfx\build.ps1
```

## Running and testing

- Profiling needs an **elevated** shell. Use a session name that is not the kernel logger name (CLR providers can't be enabled on it).
- Override config with env vars, e.g. `$env:Agent__WindowSeconds='10'`, `$env:Agent__Processes__Targets__0__Process='FxBusy.exe'`, `$env:Agent__Metrics__Enabled='false'`.
- Local Pyroscope: `http://localhost:4040`. Check results with `/pyroscope/render?query=process_cpu:cpu:nanoseconds:cpu:nanoseconds{service_name="<name>"}&from=<unix>&until=<unix>&format=json` and use short recent windows.
- Validate pprof output with `go tool pprof`.

## Conventions and gotchas

- Keep scope tight: no tracing/activities, no gzip, no features that aren't needed.
- Symbol downloads only for processes matching `Processes:Targets`; an empty list disables them. A missing PDB is logged once per module.
- Real-time ETW sessions don't populate module PDB info, so debug info is read from the PE file on disk.
- `TraceLog` is not thread-safe for concurrent reads; snapshot on the event thread, symbolize from the snapshot.
- Service names and labels are sanitized for Pyroscope (no spaces).
- Hosting packages are pinned to 10.0.x on purpose (latest NuGet is 11.x preview).
- Smart App Control can block freshly built binaries on dev machines; there is no per-folder exclusion and self-signing didn't help.
- Known gaps: NGen images (`system.ni`) aren't symbolized; VB6 p-code only shows `msvbvm60` frames; allocation and lock-contention profiles are .NET-only (CPU covers everything); COM+ apps in `dllhost` are told apart with a target's `CommandLineContains` (the AppID GUID from `/Processid:{GUID}`).
- Profile types: CPU (kernel sampling, all runtimes), plus .NET allocations (`memory:alloc_*`) and lock contention (`mutex:*`) from CLR events (`CollectAllocations` / `CollectContentions`). Also disk (`FileIORead/Write`), network and waits. Network must use the manifest provider `Microsoft-Windows-Kernel-Network` with `EventIDStacksToEnable`; classic kernel TCPIP events carry no stacks. Wait stacks come from `ThreadCSwitch` (use header `ProcessID`/`ThreadID`, not `OldProcessID`). Pyroscope names custom types from the last sample type; keep period type names equal to a real name (an unrecognised one like `wait` shows blank). The CLR provider must be re-enabled with stack options after `CaptureState` (it resets them), and user-mode event stacks arrive late, so `EtwCollector` resolves them ~1 s after the event.

- Handles: not collected. The kernel Handle keyword only works on the "NT Kernel Logger" session, which our named session can't use. Thread pool: only worker-thread adjustment events arrive over ETW (no work item enqueue/dequeue), so that profile is stackless.
