# winprof-agent

A Windows service that continuously profiles processes using ETW and pushes pprof profiles to Grafana Pyroscope.

Targets: C++, VB6 (native-compiled), .NET Framework, .NET (Core), including COM and COM+ hosts.

## Design

- .NET 10 Worker Service, runs as SYSTEM (needs `SeSystemProfilePrivilege`).
- Direct real-time ETW session via TraceEvent (kernel sampled profile with stacks, image load, CLR rundown). Does not shell out to `wpr.exe`.
- Rolling windows (`Agent:WindowSeconds`); each window is converted to one pprof profile per process and pushed to Pyroscope.
- Symbols from PDBs in `Agent:Symbols:PdbSearchPaths`.
- Configured through `appsettings.json` (plus environment variables).

## Known limits

- VB6 p-code shows only `msvbvm60` frames; native-compiled VB6 with PDBs resolves normally.
- x86 stacks without frame pointers may be incomplete.

## Status

Scaffold only. Capture, conversion and push are not yet implemented.
