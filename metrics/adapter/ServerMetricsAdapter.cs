using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("Server Metrics Adapter", "R-udren", "0.3.0")]
    [Description("Read-only framework counters and bounded local event telemetry; no Harmony patches")]
    public class ServerMetricsAdapter : RustPlugin
    {
        private Settings _settings;
        private bool _configurationValid;
        private volatile bool _running;
        private readonly Dictionary<string, Sample> _samples = new();
        private readonly Dictionary<Type, CounterReader> _readers = new();
        private readonly Queue<string> _queue = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Dictionary<string, string> _headers;
        private string _writeUrl;
        private string _framework;
        private string _batch;
        private bool _inFlight;
        private int _attempts;
        private double _retryAfter;
        private long _dropped;
        private long _uploaded;
        private long _failures;
        private long _observerErrors;
        private long _lastTimestamp;
        private double _lastPollMs;
        private int _lastReportFrame = -1;
        private bool _nativeConfigured;
        private int _previousInvokeMode;
        private int _previousFixedInvokeMode;
        private bool _previousPacketProfiling;
        private readonly Dictionary<object, double> _nativeTimes = new();
        private int[] _packetCounts;
        private int[] _packetBytes;
        private readonly object _logGate = new();
        private readonly Queue<CapturedLog> _logs = new();
        private readonly Queue<CapturedLog> _infoLogs = new();
        private readonly List<ServerLogTail> _logFiles = new();
        private readonly Dictionary<string, LogObservation> _logObservations = new(StringComparer.Ordinal);
        private UnityEngine.Application.LogCallback _logCallback;
        private long _logMinute = -1;
        private int _logsThisMinute;
        private int _infoLogsThisMinute;
        private long _logsDropped;
        private long _infoLogsDropped;
        private long _logFileErrors;
        private int _filePollInFlight;
        private int _logFileGeneration;
        private long _lastLogTimestamp;
        private static readonly Regex SteamId = new(@"\b\d{17}\b", RegexOptions.Compiled);
        private static readonly Regex Ipv4 = new(@"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?\b", RegexOptions.Compiled);
        private static readonly Regex Ipv6 = new(@"(?i)(?<![\w:])(?:[0-9a-f]{0,4}:){2,}[0-9a-f:]{0,4}(?:%\w+)?", RegexOptions.Compiled);
        private static readonly Regex Credential = new(@"(?i)\b(password|passwd|token|secret|authorization|api[_-]?key)\b[""']?(?:\s*[:=]\s*|\s+)(?:""[^""]*""|'[^']*'|[^\s,;]+)", RegexOptions.Compiled);
        private static readonly Regex BasicToken = new(@"(?i)\b(Basic|Bearer)\s+[A-Za-z0-9_+/=.-]+", RegexOptions.Compiled);
        private static readonly char[] LogLineSeparators = { '\r', '\n' };

        public sealed class Settings
        {
            public string Endpoint = "http://127.0.0.1:18086";
            public string Database = "rust-server-metrics";
            public string RetentionPolicy = "local_14d";
            public string Username = "rust_writer";
            public string Password = "";
            public string Server = "local-dev";
            public float SampleSeconds = 5;
            public int QueueLimit = 1000;
            public bool CaptureLogs = true;
            public bool CaptureInfoLogs = false;
            public bool CaptureAllServerLogs = false;
            public string[] ServerLogFiles = { "server.log" };
            public int InfoLogLimitPerMinute = 120;
            public int LogLimitPerMinute = 60;
            public bool NativeInvokeDetails = true;
            public bool NativePacketDetails = true;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new Settings(), true);
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _settings = Config.ReadObject<Settings>();
            Validate(_settings);
            _configurationValid = true;
        }

        public static void Validate(Settings settings)
        {
            if (settings == null || !Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out Uri endpoint) ||
                endpoint.Scheme != "http" || !endpoint.IsLoopback || endpoint.UserInfo.Length != 0 ||
                endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                throw new ArgumentException("Metrics Endpoint must be an HTTP loopback origin without credentials or a path");
            if (string.IsNullOrWhiteSpace(settings.Database) || string.IsNullOrWhiteSpace(settings.RetentionPolicy) ||
                string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password))
                throw new ArgumentException("Metrics database, retention policy, username and password are required");
            if (string.IsNullOrWhiteSpace(settings.Server) || settings.Server.Length > 128 ||
                settings.Server.Any(char.IsControl) || settings.Username.Contains(":"))
                throw new ArgumentException("Invalid metrics server identifier or username");
            if (float.IsNaN(settings.SampleSeconds) || float.IsInfinity(settings.SampleSeconds) ||
                settings.SampleSeconds < 2 || settings.SampleSeconds > 60 || settings.QueueLimit < 100 || settings.QueueLimit > 10000)
                throw new ArgumentException("Metrics sampling must be 2–60 seconds and queue limit 100–10000");
            if (settings.LogLimitPerMinute < 1 || settings.LogLimitPerMinute > 600)
                throw new ArgumentException("Log limit must be 1–600 messages per minute");
            if (settings.InfoLogLimitPerMinute < 1 || settings.InfoLogLimitPerMinute > 600)
                throw new ArgumentException("Info log limit must be 1–600 messages per minute");
            if (settings.ServerLogFiles == null || settings.ServerLogFiles.Length > 8 ||
                settings.ServerLogFiles.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 1000 || path.Any(char.IsControl)))
                throw new ArgumentException("ServerLogFiles must contain at most eight valid paths (or be empty)");
            foreach (var path in settings.ServerLogFiles) _ = Path.GetFullPath(path);
        }

        private void OnServerInitialized()
        {
            if (_running || !_configurationValid) return;
            Validate(_settings);
            _framework = typeof(Plugin).Assembly.GetName().Name == "Carbon.Common" ? "carbon" : "oxide";
            _headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(_settings.Username + ":" + _settings.Password)),
                ["Content-Type"] = "text/plain; charset=utf-8"
            };
            _writeUrl = _settings.Endpoint.TrimEnd('/') + "/write?db=" + Uri.EscapeDataString(_settings.Database) +
                "&rp=" + Uri.EscapeDataString(_settings.RetentionPolicy) + "&precision=ms";
            _running = true;
            Observe(ConfigureNativeDetails);
            if (_settings.CaptureLogs)
            {
                _logCallback = CaptureLog;
                UnityEngine.Application.logMessageReceivedThreaded += _logCallback;
            }
            ConfigureLogFiles();
            Observe(() => Event("adapter_started", "Metrics adapter started", "Collector start or reload; this does not establish a server restart."));
            Observe(Poll);
            timer.Every(_settings.SampleSeconds, () => Observe(Poll));
            timer.Every(0.5f, () => Observe(() => PollServer(Timestamp())));
            timer.Every(2, () => Observe(Flush));
            Puts("Local telemetry enabled: " + _framework + "; native and framework counters, no player lifecycle or Harmony instrumentation.");
        }

        private void Observe(Action action)
        {
            if (!_running) return;
            try { action(); }
            catch (Exception)
            {
                Interlocked.Increment(ref _observerErrors);
                if (_observerErrors == 1 || _observerErrors % 100 == 0)
                    PrintWarning("Metrics observation failed; gameplay callback continues. See metricsadapter.status.");
            }
        }

        private long Timestamp()
        {
            // Distinct millisecond keys prevent same-series events overwriting one another.
            _lastTimestamp = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _lastTimestamp + 1);
            return _lastTimestamp;
        }

        private string Tags(string extra = "")
        {
            return ",server=" + EscapeTag(_settings.Server) + ",framework=" + _framework + extra;
        }

        private void Add(string measurement, string fields, long timestamp, string extra = "")
        {
            var line = measurement + Tags(extra) + " " + fields + " " + timestamp.ToString(CultureInfo.InvariantCulture);
            if (Encoding.UTF8.GetByteCount(line) > 16000) { _dropped++; return; }
            if (_queue.Count >= _settings.QueueLimit) { _queue.Dequeue(); _dropped++; }
            _queue.Enqueue(line);
        }

        private void Event(string kind, string title, string text, string plugin = "")
        {
            Add("server_events", "title=" + Quote(title) + ",text=" + Quote(title + ": " + text) +
                ",tags=" + Quote(kind) + ",plugin=" + Quote(plugin), Timestamp(), ",event=" + kind);
        }

        private void Poll()
        {
            var started = _clock.Elapsed.TotalMilliseconds;
            var now = _clock.Elapsed.TotalSeconds;
            var timestamp = Timestamp();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var activeTypes = new HashSet<Type>();
            foreach (var plugin in plugins.GetAll())
            {
                if (plugin == null || ReferenceEquals(plugin, this) || !plugin.IsLoaded) continue;
                activeTypes.Add(plugin.GetType());
                if (!_readers.TryGetValue(plugin.GetType(), out CounterReader reader))
                {
                    reader = new CounterReader(plugin.GetType());
                    _readers.Add(plugin.GetType(), reader);
                }
                if (reader.IsCore(plugin)) continue;
                seen.Add(plugin.Name);
                Observe(() => PollPlugin(plugin, reader, now, timestamp));
            }
            foreach (var name in _samples.Keys.Where(name => !seen.Contains(name)).ToArray()) _samples.Remove(name);
            foreach (var type in _readers.Keys.Where(type => !activeTypes.Contains(type)).ToArray()) _readers.Remove(type);
            Observe(() => PollServer(timestamp));
            Observe(() => PollNativeDetails(timestamp));
            Observe(PollLogFiles);
            Observe(DrainLogs);
            _lastPollMs = _clock.Elapsed.TotalMilliseconds - started;
            Add("adapter_health", "queued=" + _queue.Count + "i,dropped=" + _dropped + "i,uploaded=" + _uploaded +
                "i,upload_failures=" + _failures + "i,observer_errors=" + _observerErrors + "i,plugins=" + seen.Count +
                "i,logs_dropped=" + Interlocked.Read(ref _logsDropped) + "i,log_capture=" + Bool(_settings.CaptureLogs) +
                ",all_log_capture=" + Bool(_settings.CaptureLogs && _settings.CaptureAllServerLogs) +
                ",info_logs_dropped=" + Interlocked.Read(ref _infoLogsDropped) + "i,log_file_errors=" + Interlocked.Read(ref _logFileErrors) + "i" +
                ",poll_ms=" + Number(_lastPollMs), timestamp);
        }

        private void PollPlugin(Plugin plugin, CounterReader reader, double now, long timestamp)
        {
            var current = reader.Read(plugin, now);
            _samples.TryGetValue(plugin.Name, out Sample previous);
            var rate = Calculate(previous, current);
            var fields = "tracked_ms_total=" + Number(current.Milliseconds) + ",has_calls=" + Bool(current.Calls.HasValue) +
                ",has_exceptions=" + Bool(current.Exceptions.HasValue) + ",has_lag_spikes=" + Bool(current.LagSpikes.HasValue) +
                ",interval_valid=" + Bool(rate.Valid) + ",title=" + Quote(plugin.Title);
            if (rate.Valid)
            {
                fields += ",tracked_ms_per_second=" + Number(rate.MillisecondsPerSecond) + ",sample_seconds=" + Number(rate.Seconds);
                if (current.Calls.HasValue) fields += ",calls_per_second=" + Number(rate.CallsPerSecond);
                if (current.Exceptions.HasValue) fields += ",exceptions_delta=" + rate.Exceptions + "i";
                if (current.LagSpikes.HasValue) fields += ",lag_spikes_delta=" + rate.LagSpikes + "i";
                if (rate.Exceptions > 0)
                    Event("plugin_exceptions_observed", plugin.Name + " hook exceptions observed",
                        rate.Exceptions + " new exceptions since the previous sample (" + Number(rate.Seconds) +
                        " seconds). Timestamp is detection time; no exception stack or exact occurrence time collected.", plugin.Name);
            }
            else if (previous != null)
                Event("counter_reset", plugin.Name + " counter baseline reset", "Plugin instance or cumulative counter changed; interval rates omitted.", plugin.Name);
            _samples[plugin.Name] = current;
            Add("plugin_runtime", fields, timestamp, ",plugin=" + EscapeTag(plugin.Name));
            Add("oxide_plugins", "hookTime=" + Number(current.Milliseconds / 1000), timestamp,
                ",plugin=" + EscapeTag(plugin.Name));
        }

        private void PollServer(long timestamp)
        {
            var report = Performance.report;
            if (report.frameID == _lastReportFrame) return;
            _lastReportFrame = report.frameID;
            if (report.frameRate > 0) Add("framerate", "instant=" + Number(report.frameRate), timestamp);
            if (report.frameTime > 0 && !float.IsNaN(report.frameTime) && !float.IsInfinity(report.frameTime))
                Add("frametime", "instant=" + Number(report.frameTime), timestamp);
            if (report.memoryUsageSystem > 0)
                Add("memory", "used=" + report.memoryUsageSystem + "i,collections=" + report.memoryCollections +
                    "i,allocations=" + report.memoryAllocations + "i,gc=" + Bool(report.gcTriggered), timestamp);
            Add("tasks", "load_balancer=" + report.loadBalancerTasks + "i,invoke_handler=" + report.invokeHandlerTasks +
                "i,workshop_skins_queue=" + report.workshopSkinsQueued + "i", timestamp);
            Add("entities", "count=" + BaseNetworkable.serverEntities.Count + "i", timestamp);
            var manager = ServerMgr.Instance;
            if (manager != null && manager.connectionQueue != null)
                Add("players", "count=" + BasePlayer.activePlayerList.Count + "i,joining=" + manager.connectionQueue.Joining +
                    "i,queued=" + manager.connectionQueue.Queued + "i", timestamp);
            var network = Network.Net.sv;
            if (network != null)
            {
                Add("network", "bytes_received=" + network.GetStat(null, Network.BaseNetwork.StatTypeLong.BytesReceived_LastSecond) +
                    "i,bytes_sent=" + network.GetStat(null, Network.BaseNetwork.StatTypeLong.BytesSent_LastSecond) +
                    "i,packet_loss=" + network.GetStat(null, Network.BaseNetwork.StatTypeLong.PacketLossLastSecond) + "i", timestamp);
            }
            var phases = report.performanceSample;
            if (report.frameRate > 0)
            {
                AddPhase("Update", phases.Update, timestamp);
                AddPhase("LateUpdate", phases.LateUpdate, timestamp);
                AddPhase("FixedUpdate", phases.FixedUpdate, timestamp);
                AddPhase("PhysicsUpdate", phases.PhysicsUpdate, timestamp);
            }
        }

        private void AddPhase(string phase, TimeSpan duration, long timestamp)
        {
            Add("server_update", "duration=" + Number(duration.TotalMilliseconds), timestamp,
                ",behaviour=NativeFramePhases,method=" + phase);
        }

        private void ConfigureNativeDetails()
        {
            _previousInvokeMode = InvokeProfiler.update.mode;
            _previousFixedInvokeMode = InvokeProfiler.fixedUpdate.mode;
            _previousPacketProfiling = Network.PacketProfiler.enabled;
            _nativeConfigured = true;
            if (_settings.NativeInvokeDetails)
            {
                InvokeProfiler.update.mode = 2;
                InvokeProfiler.fixedUpdate.mode = 2;
            }
            if (_settings.NativePacketDetails) Network.PacketProfiler.enabled = true;
        }

        private void PollNativeDetails(long timestamp)
        {
            var network = Network.Net.sv;
            if (network != null)
            {
                foreach (var player in BasePlayer.activePlayerList)
                {
                    if (player == null || player.net == null || player.net.connection == null) continue;
                    var connection = player.net.connection;
                    Add("connection_latency", "ping=" + network.GetAveragePing(connection) + "i,packet_loss=" +
                        network.GetStat(connection, Network.BaseNetwork.StatTypeLong.PacketLossLastSecond) + "i", timestamp,
                        ",steamid=" + connection.userid);
                }
            }
            var seen = new HashSet<object>();
            var durations = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var profiler in new[] { InvokeProfiler.update, InvokeProfiler.fixedUpdate })
            {
                if (profiler == null || profiler.mode < 2) continue;
                foreach (var data in profiler.trackingDataList)
                {
                    var behaviour = data.Key.Type == null ? data.TypeName : data.Key.Type.Name;
                    NativeDuration(data, data.ExecutionTime.TotalMilliseconds, "invoke_execution", behaviour,
                        data.Key.MethodName, durations, seen);
                }
            }
            foreach (var queue in ObjectWorkQueue.All)
                NativeDuration(queue, queue.TotalExecutionTime.TotalMilliseconds, "work_queue", "NativeQueueCycle", queue.Name, durations, seen);
            foreach (var queue in PersistentObjectWorkQueue.All)
                NativeDuration(queue, queue.TotalExecutionTime.TotalMilliseconds, "work_queue", "NativePersistentQueueCycle", queue.Name, durations, seen);
            foreach (var key in _nativeTimes.Keys.Where(key => !seen.Contains(key)).ToArray()) _nativeTimes.Remove(key);
            foreach (var duration in durations)
            {
                var split = duration.Key.IndexOf(',');
                Add(duration.Key.Substring(0, split), "duration=" + Number(duration.Value), timestamp,
                    duration.Key.Substring(split));
            }
            if (!Network.PacketProfiler.enabled) return;
            var counts = Network.PacketProfiler.outboundSum;
            var bytes = Network.PacketProfiler.outboundBytes;
            if (_packetCounts != null && counts.Length == _packetCounts.Length && bytes.Length == _packetBytes.Length)
            {
                var fields = new StringBuilder();
                for (var index = 0; index < Math.Min(counts.Length, (int)Network.Message.Type.Count); index++)
                {
                    if (counts[index] < _packetCounts[index] || bytes[index] < _packetBytes[index]) continue;
                    var name = ((Network.Message.Type)index).ToString();
                    if (fields.Length > 0) fields.Append(',');
                    fields.Append(name).Append('=').Append(counts[index] - _packetCounts[index]).Append("i,")
                        .Append(name).Append("_bytes=").Append(bytes[index] - _packetBytes[index]).Append('i');
                }
                if (fields.Length > 0) Add("network_updates", fields.ToString(), timestamp);
            }
            _packetCounts = (int[])counts.Clone();
            _packetBytes = (int[])bytes.Clone();
        }

        private void NativeDuration(object identity, double total, string measurement, string behaviour,
            string method, Dictionary<string, double> durations, HashSet<object> seen)
        {
            if (!Finite(total) || total < 0) return;
            seen.Add(identity);
            var valid = _nativeTimes.TryGetValue(identity, out double previous) && total >= previous;
            _nativeTimes[identity] = total;
            if (!valid || total == previous) return;
            var key = measurement + ",behaviour=" + EscapeTag(behaviour) + ",method=" + EscapeTag(method);
            durations.TryGetValue(key, out double existing);
            durations[key] = existing + total - previous;
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            Observe(() => { if (plugin != null && !ReferenceEquals(plugin, this)) Event("plugin_loaded", plugin.Name + " loaded", "Framework plugin load observed.", plugin.Name); });
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            Observe(() =>
            {
                if (plugin == null || ReferenceEquals(plugin, this)) return;
                _samples.Remove(plugin.Name);
                Event("plugin_unloaded", plugin.Name + " unloaded", "Framework plugin unload observed.", plugin.Name);
            });
        }

        private void OnServerSave()
        {
            Observe(() => Event("save_observed", "World save observed", "OnServerSave callback observed; this is not a measured save duration or completion."));
        }

        private void Unload()
        {
            _running = false;
            _nativeTimes.Clear();
            if (_logCallback != null)
            {
                UnityEngine.Application.logMessageReceivedThreaded -= _logCallback;
                _logCallback = null;
            }
            lock (_logGate) { _logs.Clear(); _infoLogs.Clear(); _logObservations.Clear(); }
            _logFiles.Clear();
            _queue.Clear();
            _samples.Clear();
            _readers.Clear();
            _batch = null;
            if (_nativeConfigured)
            {
                if (_settings.NativeInvokeDetails && InvokeProfiler.update != null && InvokeProfiler.update.mode == 2) InvokeProfiler.update.mode = _previousInvokeMode;
                if (_settings.NativeInvokeDetails && InvokeProfiler.fixedUpdate != null && InvokeProfiler.fixedUpdate.mode == 2) InvokeProfiler.fixedUpdate.mode = _previousFixedInvokeMode;
                if (_settings.NativePacketDetails && Network.PacketProfiler.enabled) Network.PacketProfiler.enabled = _previousPacketProfiling;
            }
            // Framework destroys this plugin's timers and owner-associated web requests.
        }

        [ConsoleCommand("metricsadapter.status")]
        private void Status(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) return;
            arg.ReplyWith("framework=" + _framework + " running=" + _running + " plugins=" + _samples.Count +
                " queued=" + _queue.Count + " in_flight=" + _inFlight + " uploaded_batches=" + _uploaded +
                " dropped_points=" + _dropped + " upload_failures=" + _failures + " observer_errors=" + _observerErrors +
                " log_capture=" + _settings.CaptureLogs + " dropped_logs=" + Interlocked.Read(ref _logsDropped) +
                " all_log_capture=" + _settings.CaptureAllServerLogs + " dropped_info_logs=" + Interlocked.Read(ref _infoLogsDropped) +
                " log_file_errors=" + Interlocked.Read(ref _logFileErrors) +
                " last_poll_ms=" + Number(_lastPollMs));
        }

        [ConsoleCommand("metricsadapter.annotate")]
        private void Annotate(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) return;
            if (!_running) { arg.ReplyWith("Metrics adapter is inactive."); return; }
            var message = arg.Args == null ? "" : string.Join(" ", arg.Args);
            if (string.IsNullOrWhiteSpace(message)) { arg.ReplyWith("Usage: metricsadapter.annotate <operator note; avoid secrets/player identifiers>"); return; }
            Observe(() => Event("operator_note", "Operator note", message));
            arg.ReplyWith("Annotation queued.");
        }

        private void Flush()
        {
            if (_inFlight || _clock.Elapsed.TotalSeconds < _retryAfter) return;
            if (_batch == null)
            {
                if (_queue.Count == 0) return;
                var builder = new StringBuilder();
                var bytes = 0;
                while (_queue.Count > 0)
                {
                    var next = _queue.Peek();
                    var size = Encoding.UTF8.GetByteCount(next) + 1;
                    if (bytes + size > 64000) break;
                    builder.Append(_queue.Dequeue()).Append('\n');
                    bytes += size;
                }
                _batch = builder.ToString();
                _attempts = 0;
            }
            _inFlight = true;
            _attempts++;
            try
            {
                webrequest.Enqueue(_writeUrl, _batch, (code, response) =>
                {
                    if (!_running) return;
                    try { NextTick(() => Observe(() => CompleteUpload(code))); }
                    catch (Exception) { Interlocked.Increment(ref _observerErrors); }
                }, this, RequestMethod.POST, _headers, 10);
            }
            catch (Exception)
            {
                CompleteUpload(0);
            }
        }

        private void CompleteUpload(int code)
        {
            _inFlight = false;
            if (code == 204)
            {
                _uploaded++;
                _batch = null;
                _retryAfter = 0;
                return;
            }
            _failures++;
            if (_failures == 1 || _failures % 100 == 0)
                PrintWarning("Metrics upload failed (HTTP " + code + "); bounded retries, no gameplay interruption.");
            _retryAfter = _clock.Elapsed.TotalSeconds + Math.Min(30, _attempts * 5);
            if (_attempts >= 3 || (code >= 400 && code < 500 && code != 429))
            {
                _dropped += _batch.Count(c => c == '\n');
                _batch = null;
            }
        }

        private sealed class CapturedLog
        {
            public long Timestamp;
            public string Severity;
            public string Message;
            public string Stack;
            public string Source;
            public string Details;
            public string Origin;
        }

        [ConsoleCommand("metricsadapter.logs")]
        private void LogCapture(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) return;
            if (arg.Args == null || arg.Args.Length != 2 || arg.Args[0] != "all" ||
                (arg.Args[1] != "on" && arg.Args[1] != "off"))
            { arg.ReplyWith("Usage: metricsadapter.logs all on|off"); return; }
            _settings.CaptureAllServerLogs = arg.Args[1] == "on";
            Config.WriteObject(_settings, true);
            ConfigureLogFiles();
            Observe(PollLogFiles);
            arg.ReplyWith("Other server log collection " + (_settings.CaptureAllServerLogs ? "enabled" : "disabled") +
                "; warnings and errors retain their existing setting.");
        }

        private void ConfigureLogFiles()
        {
            Interlocked.Increment(ref _logFileGeneration);
            _logFiles.Clear();
            if (!_settings.CaptureLogs || !_settings.CaptureAllServerLogs) return;
            foreach (var path in _settings.ServerLogFiles.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
                _logFiles.Add(new ServerLogTail(path));
        }

        private void PollLogFiles()
        {
            if (_logFiles.Count == 0 || Interlocked.CompareExchange(ref _filePollInFlight, 1, 0) != 0) return;
            var files = _logFiles.ToArray();
            var generation = Volatile.Read(ref _logFileGeneration);
            try
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        foreach (var file in files)
                        {
                            if (!_running || generation != Volatile.Read(ref _logFileGeneration)) return;
                            try
                            {
                                file.Read(line =>
                                {
                                    if (generation == Volatile.Read(ref _logFileGeneration))
                                        CaptureLogEntry(line, "", FileLogType(line), "file:" + Path.GetFileName(file.Path));
                                });
                            }
                            catch (IOException) { Interlocked.Increment(ref _logFileErrors); }
                            catch (UnauthorizedAccessException) { Interlocked.Increment(ref _logFileErrors); }
                        }
                    }
                    catch (Exception) { Interlocked.Increment(ref _logFileErrors); }
                    finally { Interlocked.Exchange(ref _filePollInFlight, 0); }
                });
            }
            catch { Interlocked.Exchange(ref _filePollInFlight, 0); throw; }
        }

        public static UnityEngine.LogType FileLogType(string line)
        {
            var value = (line ?? "").TrimStart();
            if (Regex.IsMatch(value, @"^(?:\[[^\]]{1,40}\]\s*){0,2}\[?(?:warning|warn)\b", RegexOptions.IgnoreCase)) return UnityEngine.LogType.Warning;
            if (Regex.IsMatch(value, @"^(?:\[[^\]]{1,40}\]\s*){0,2}\[?(?:error|erro|exception|assert)\b", RegexOptions.IgnoreCase)) return UnityEngine.LogType.Error;
            return UnityEngine.LogType.Log;
        }

        public sealed class ServerLogTail
        {
            public string Path { get; }
            private long _offset = -1;
            private DateTime _created;
            private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
            private readonly StringBuilder _pending = new();
            private bool _discardLine;
            private readonly byte[] _buffer = new byte[4096];
            private readonly char[] _characters = new char[4098];

            public ServerLogTail(string path) { Path = path; }

            public void Read(Action<string> capture)
            {
                using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var created = File.GetCreationTimeUtc(Path);
                if (_offset < 0)
                {
                    _offset = file.Length;
                    _created = created;
                    if (_offset > 0) { file.Seek(-1, SeekOrigin.End); _discardLine = file.ReadByte() != '\n'; }
                    return;
                }
                if (file.Length < _offset || created != _created)
                { _offset = 0; _pending.Clear(); _decoder.Reset(); _discardLine = false; }
                _created = created;
                file.Seek(_offset, SeekOrigin.Begin);
                var remaining = 65536;
                while (remaining > 0)
                {
                    var read = file.Read(_buffer, 0, Math.Min(_buffer.Length, remaining));
                    if (read == 0) return;
                    _offset += read;
                    remaining -= read;
                    var count = _decoder.GetChars(_buffer, 0, read, _characters, 0);
                    for (var index = 0; index < count; index++)
                    {
                        var character = _characters[index];
                        if (character == '\n')
                        {
                            if (!_discardLine && _pending.Length > 0) capture(_pending.ToString());
                            _pending.Clear();
                            _discardLine = false;
                        }
                        else if (character != '\r' && !_discardLine && _pending.Length < 4000) _pending.Append(character);
                    }
                }
            }
        }

        private sealed class LogObservation
        {
            public string Origin;
            public long Timestamp;
        }

        private void CaptureLog(string condition, string stackTrace, UnityEngine.LogType type)
        {
            CaptureLogEntry(condition, stackTrace, type, "unity");
        }

        private void CaptureLogEntry(string condition, string stackTrace, UnityEngine.LogType type, string origin)
        {
            if (!_running || string.IsNullOrWhiteSpace(condition)) return;
            if (origin.StartsWith("file:", StringComparison.Ordinal) && !_settings.CaptureAllServerLogs) return;
            var info = type == UnityEngine.LogType.Log;
            if (info && !_settings.CaptureInfoLogs && !_settings.CaptureAllServerLogs) return;
            var searchLength = Math.Min(condition.Length, 4000);
            if (condition.IndexOf("ServerMetricsAdapter", 0, searchLength, StringComparison.OrdinalIgnoreCase) >= 0 ||
                condition.IndexOf("[Server Metrics Adapter]", 0, searchLength, StringComparison.OrdinalIgnoreCase) >= 0) return;
            var severity = type == UnityEngine.LogType.Warning ? "warning" :
                type == UnityEngine.LogType.Error ? "error" : type == UnityEngine.LogType.Exception ? "exception" :
                type == UnityEngine.LogType.Assert ? "assert" : "info";
            try
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                lock (_logGate)
                {
                    if (!_running) return;
                    var minute = timestamp / 60000;
                    if (minute != _logMinute) { _logMinute = minute; _logsThisMinute = 0; _infoLogsThisMinute = 0; }
                    var queue = info ? _infoLogs : _logs;
                    if ((info ? _infoLogsThisMinute >= _settings.InfoLogLimitPerMinute : _logsThisMinute >= _settings.LogLimitPerMinute) || queue.Count >= 200)
                    {
                        if (info) Interlocked.Increment(ref _infoLogsDropped);
                        else Interlocked.Increment(ref _logsDropped);
                        return;
                    }
                    var formatted = FormatLog(condition, stackTrace, _settings.Password);
                    if (_logObservations.TryGetValue(formatted.Message, out LogObservation previous) &&
                        previous.Origin != origin && timestamp - previous.Timestamp < 10000) return;
                    if (_logObservations.Count >= 512) _logObservations.Clear();
                    _logObservations[formatted.Message] = new LogObservation { Origin = origin, Timestamp = timestamp };
                    if (info) _infoLogsThisMinute++;
                    else _logsThisMinute++;
                    queue.Enqueue(new CapturedLog
                    {
                        Timestamp = timestamp,
                        Severity = severity,
                        Message = formatted.Message,
                        Stack = string.Join(" | ", formatted.Frames),
                        Source = formatted.Source,
                        Details = formatted.Details,
                        Origin = origin
                    });
                }
            }
            catch (Exception) { Interlocked.Increment(ref _logsDropped); }
        }

        private void DrainLogs()
        {
            while (true)
            {
                CapturedLog log;
                lock (_logGate)
                {
                    if (_logs.Count == 0 && _infoLogs.Count == 0) return;
                    log = _logs.Count > 0 ? _logs.Dequeue() : _infoLogs.Dequeue();
                }
                _lastLogTimestamp = Math.Max(log.Timestamp, _lastLogTimestamp + 1);
                Add("server_logs", "title=" + Quote(log.Severity + " log") + ",message=" + Quote(log.Message) +
                    ",summary=" + Quote(log.Message) + ",source=" + Quote(log.Source) + ",origin=" + Quote(log.Origin) + ",details_json=" + Quote(log.Details, 6000) +
                    ",text=" + Quote(log.Message) + ",stack_trace=" + Quote(log.Stack) + ",observed_utc_ms=" + log.Timestamp + "i",
                    _lastLogTimestamp, ",severity=" + log.Severity);
            }
        }

        public static string Redact(string text, string password)
        {
            var value = text ?? "";
            if (!string.IsNullOrEmpty(password)) value = value.Replace(password, "[credential]");
            value = BasicToken.Replace(value, "[authorization]");
            value = Credential.Replace(value, "$1=[credential]");
            value = SteamId.Replace(value, "[player-id]");
            value = Ipv4.Replace(value, "[ip]");
            value = Ipv6.Replace(value, "[ip]");
            return Clean(value, 1000);
        }

        public sealed class FormattedLog
        {
            public string Message;
            public string Source;
            public string[] Frames;
            public string Details;
        }

        public static FormattedLog FormatLog(string condition, string stack, string password)
        {
            var lines = new string((condition ?? "").Take(4000).ToArray())
                .Split(LogLineSeparators, StringSplitOptions.RemoveEmptyEntries);
            var message = Redact(lines.Length == 0 ? "" : lines[0].Trim(), password);
            var exceptionPattern = @"\b(?:[A-Za-z_]\w*\.)*[A-Za-z_]\w*Exception\b";
            var exception = Regex.Match(message, exceptionPattern).Value;
            var candidates = lines.Skip(1).Concat(new string((stack ?? "").Take(4000).ToArray())
                .Split(LogLineSeparators, StringSplitOptions.RemoveEmptyEntries));
            var frames = new List<string>();
            var omitted = 0;
            foreach (var candidate in candidates)
            {
                var frame = candidate.Trim();
                if (frame.Length == 0) continue;
                if (Regex.IsMatch(frame, "^" + exceptionPattern + @"\s*:"))
                {
                    if (exception.Length == 0) exception = Regex.Match(frame, exceptionPattern).Value;
                    message = Clean(message + " · " + Redact(frame, password), 1000);
                    continue;
                }
                if (!Regex.IsMatch(frame, @"[.:][\w<>+`]+\s*\("))
                {
                    message = Clean(message + " · " + Redact(frame, password), 1000);
                    continue;
                }
                if (frame.StartsWith("UnityEngine.Debug", StringComparison.Ordinal) ||
                    frame.StartsWith("UnityEngine.Logger", StringComparison.Ordinal) ||
                    frame.IndexOf("System.Reflection.", StringComparison.Ordinal) >= 0 ||
                    frame.IndexOf("MonoMod.Utils.DynamicMethodDefinition", StringComparison.Ordinal) >= 0 ||
                    frame.IndexOf("<>c__DisplayClass", StringComparison.Ordinal) >= 0)
                { omitted++; continue; }
                if (frames.Count >= 8) { omitted++; continue; }
                frame = Regex.Replace(frame, @"\s+\(at .*[/\\]([^/\\]+:\d+)\)$", " · $1");
                frame = Regex.Replace(frame, @"\s+in .*[/\\]([^/\\]+):line (\d+)\)?$", " · $1:$2");
                frame = Regex.Replace(frame, @"^at\s+(?:(?:void|bool|object|int|string|double)\s+)?", "");
                frame = frame.Replace("Oxide.Plugins.", "").Replace(":", ".");
                frame = Regex.Replace(frame, @"(\.cs)\.(\d+)$", "$1:$2");
                frames.Add(Clean(Redact(frame, password), 180));
            }
            var source = frames.Count == 0 ? "" : frames[0];
            var details = "{\"message\":" + Quote(message) + ",\"exception\":" + Quote(exception) + ",\"source\":" + Quote(source) +
                ",\"frames\":[" + string.Join(",", frames.Select(frame => Quote(frame))) + "],\"omitted_frames\":" + omitted + "}";
            return new FormattedLog { Message = message, Source = source, Frames = frames.ToArray(), Details = details };
        }

        public sealed class CounterReader
        {
            private readonly MemberInfo _time;
            private readonly MemberInfo _calls;
            private readonly MemberInfo _exceptions;
            private readonly MemberInfo _lag;
            private readonly MemberInfo _core;

            public CounterReader(Type type)
            {
                _time = Find(type, "TotalHookTime");
                _calls = Find(type, "TotalHookFires");
                _exceptions = Find(type, "TotalHookExceptions");
                _lag = Find(type, "TotalHookLagSpikes");
                _core = Find(type, "IsCorePlugin");
                if (_time == null) throw new InvalidOperationException("Framework does not expose TotalHookTime");
            }

            private static MemberInfo Find(Type type, string name)
            {
                return (MemberInfo)type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public) ??
                    type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            }

            private static object Get(MemberInfo member, object plugin)
            {
                if (member == null) return null;
                var property = member as PropertyInfo;
                return property != null ? property.GetValue(plugin, null) : ((FieldInfo)member).GetValue(plugin);
            }

            public bool IsCore(object plugin) { return Equals(Get(_core, plugin), true); }

            public Sample Read(object plugin, double now)
            {
                var raw = Get(_time, plugin);
                if (raw is not TimeSpan && raw is not double) throw new InvalidOperationException("Unsupported framework time counter type");
                var milliseconds = raw is TimeSpan span ? span.TotalMilliseconds : Convert.ToDouble(raw, CultureInfo.InvariantCulture) * 1000;
                if (!Finite(milliseconds) || milliseconds < 0) throw new InvalidOperationException("Invalid framework time counter");
                return new Sample
                {
                    Instance = plugin,
                    Seconds = now,
                    Milliseconds = milliseconds,
                    Calls = ReadCount(_calls, plugin),
                    Exceptions = ReadCount(_exceptions, plugin),
                    LagSpikes = ReadCount(_lag, plugin)
                };
            }

            private static long? ReadCount(MemberInfo member, object plugin)
            {
                if (member == null) return null;
                var value = Convert.ToInt64(Get(member, plugin), CultureInfo.InvariantCulture);
                if (value < 0) throw new InvalidOperationException("Negative framework counter");
                return value;
            }
        }

        public sealed class Sample
        {
            public object Instance;
            public double Seconds;
            public double Milliseconds;
            public long? Calls;
            public long? Exceptions;
            public long? LagSpikes;
        }

        public sealed class Rate
        {
            public bool Valid;
            public double Seconds;
            public double MillisecondsPerSecond;
            public double CallsPerSecond;
            public long Exceptions;
            public long LagSpikes;
        }

        public static Rate Calculate(Sample previous, Sample current)
        {
            var result = new Rate();
            if (previous == null || current == null || !ReferenceEquals(previous.Instance, current.Instance)) return result;
            var seconds = current.Seconds - previous.Seconds;
            if (!Finite(seconds) || seconds <= 0 || !Finite(current.Milliseconds) || !Finite(previous.Milliseconds) ||
                current.Milliseconds < previous.Milliseconds || current.Calls < previous.Calls ||
                current.Exceptions < previous.Exceptions || current.LagSpikes < previous.LagSpikes) return result;
            result.Valid = true;
            result.Seconds = seconds;
            result.MillisecondsPerSecond = (current.Milliseconds - previous.Milliseconds) / seconds;
            result.CallsPerSecond = (current.Calls.GetValueOrDefault() - previous.Calls.GetValueOrDefault()) / seconds;
            result.Exceptions = current.Exceptions.GetValueOrDefault() - previous.Exceptions.GetValueOrDefault();
            result.LagSpikes = current.LagSpikes.GetValueOrDefault() - previous.LagSpikes.GetValueOrDefault();
            return result;
        }

        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static string Bool(bool value) { return value ? "true" : "false"; }
        private static string Number(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }

        public static string EscapeTag(string value)
        {
            return Clean(value, 128).Replace("\\", "\\\\").Replace(" ", "\\ ").Replace(",", "\\,").Replace("=", "\\=");
        }

        public static string Quote(string value)
        {
            return Quote(value, 1000);
        }

        private static string Quote(string value, int limit)
        {
            return "\"" + Clean(value, limit).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string Clean(string value, int limit)
        {
            value ??= "";
            return new string(value.Take(limit).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        }
    }
}
