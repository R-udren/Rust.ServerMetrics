# Rust Server Metrics

Carbon/Oxide collector with a local Grafana and InfluxDB stack, based on
[RustyMoose/Rust.ServerMetrics](https://github.com/RustyMoose/Rust.ServerMetrics).
Dashboards cover server performance, plugin impact, events, and searchable logs.

## Install

Requires Docker Compose, Python 3, and a Windows Rust server with Carbon or Oxide.
From this repository, run setup, wait for the collector to load, then verify:

```bat
metrics\setup.bat --server-dir "C:\Rust\Server" --framework carbon
metrics\verify.bat --require-metrics
```

Use `--framework oxide` for Oxide. Setup generates credentials in the ignored
`metrics/.env`, starts the containers, and installs `ServerMetricsAdapter.cs`.
Existing credentials are preserved. `--stack-only` starts the dashboards without
installing the collector; omit `--require-metrics` when verifying that mode.

- [Grafana](http://127.0.0.1:13001): user `localadmin`, password in `metrics/.env`.
- [InfluxDB](http://127.0.0.1:18086): authenticated database with 14-day retention.

Both services bind to loopback. The plugin can hotload while Rust runs. If the
framework watcher is disabled, use `c.load ServerMetricsAdapter` or
`oxide.load ServerMetricsAdapter`. Setup refuses to install alongside the legacy
`HarmonyMods/RustServerMetrics.dll`; remove that DLL only while Rust is stopped.

## Configuration and commands

Setup writes configuration relative to the Rust server directory:

| Framework | Configuration                              |
| --------- | ------------------------------------------ |
| Carbon    | `carbon/configs/ServerMetricsAdapter.json` |
| Oxide     | `oxide/config/ServerMetricsAdapter.json`   |

Set `Server` to a distinct name for each server. Existing settings survive setup;
the writer password is refreshed from `metrics/.env`. After editing a running
collector's configuration, use `c.reload ServerMetricsAdapter` or
`oxide.reload ServerMetricsAdapter`.

Run these commands in the server console:

| Command                          | Purpose                                                                        |
| -------------------------------- | ------------------------------------------------------------------------------ |
| `metricsadapter.status`          | Show collection state, queued/dropped points, upload failures, and log health. |
| `metricsadapter.annotate <note>` | Add an event marker; exclude secrets and player identifiers.                   |
| `metricsadapter.logs all on`     | Enable routine Unity/framework output and configured log files.                |
| `metricsadapter.logs all off`    | Disable extra collection, preserving the warning/error setting.                |

## Collection and limits

The collector samples native performance, entity/task counts, invoke/work-queue
timings, network traffic, and passive player latency. Plugin impact uses existing
framework elapsed-time counters; coverage differs between frameworks and is not
total plugin CPU. Save observations, plugin changes, and operator notes appear as
events. RPC/command timings and client FPS/memory require additional instrumentation.
The legacy Harmony source and detailed dashboard remain available, but setup
installs only the ordinary framework plugin.

Defaults are a five-second sample interval and a 1,000-point in-memory queue
(`SampleSeconds`, `QueueLimit`). A full queue drops its oldest points. Upload batches
are dropped after three failed attempts or a non-429 HTTP 4xx response; this buffer
is not durable across unloads. Check `metricsadapter.status` for drops and failures.

Warnings, errors, and exceptions are collected and redacted by default. File-only
logs and output from before the collector loaded are excluded. Extra log collection
is opt-in through `metricsadapter.logs all on`; the command saves its setting and
applies without restarting Rust.

- `ServerLogFiles` accepts up to eight paths, relative to the server working
  directory or absolute; the default is `server.log`. File capture begins at the end,
  handles rotation/truncation, and uses collection time.
- Matching Unity/file messages are deduplicated across streams within ten seconds.
  Raw console output must be written to a configured log file to be captured.
- Routine output has a separate queue and `InfoLogLimitPerMinute` of 120; warnings
  and errors use `LogLimitPerMinute`, default 60. Capture failures and drops appear
  in the logs dashboard.
- Dashboard toggles **Other server logs** and **Show other logs** are off by default.
  They control display only, not collection.

## Development

Run `metrics\check.bat` for both framework builds, tests, analyzers, and formatting.
Requires .NET SDK 10, uv, Bun, and local game/framework assemblies. The
[adapter project](metrics/adapter/Adapter.csproj) accepts `GameManaged`,
`CarbonManaged`, and `OxideManaged` overrides for other installations.

MIT license, copyright 2021 Vital Rust; see [LICENSE](LICENSE). The
[upstream repository](https://github.com/RustyMoose/Rust.ServerMetrics) contains the
legacy Harmony setup guide.
