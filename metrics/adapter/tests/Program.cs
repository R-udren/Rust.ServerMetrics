using System.Reflection;
using AdapterTests;
using Oxide.Plugins;
using static Oxide.Plugins.ServerMetricsAdapter;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}
static void Invoke(object target, string name, params object[] arguments)
{
    typeof(ServerMetricsAdapter).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
static object Field(object target, string name)
{
    return typeof(ServerMetricsAdapter).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
}
static void Set(object target, string name, object value)
{
    typeof(ServerMetricsAdapter).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}

var carbon = new CarbonCounter { TotalHookTime = TimeSpan.FromMilliseconds(100), TotalHookFires = 10 };
var reader = new CounterReader(carbon.GetType());
var baseline = reader.Read(carbon, 1);
carbon.TotalHookTime = TimeSpan.FromMilliseconds(160);
carbon.TotalHookFires = 12;
carbon.TotalHookExceptions = 1;
var rate = Calculate(baseline, reader.Read(carbon, 4));
Check(rate.Valid && rate.MillisecondsPerSecond == 20 && rate.CallsPerSecond == 2d / 3 && rate.Exceptions == 1,
    "Carbon TimeSpan units and real elapsed interval (timer delay) preserved");
var oxide = new OxideCounter { TotalHookTime = 0.16 };
var oxideRead = new CounterReader(oxide.GetType()).Read(oxide, 1);
Check(oxideRead.Milliseconds == 160 && oxideRead.Calls == null && oxideRead.Exceptions == null,
    "Oxide seconds converted; unsupported counters remain absent");
Check(!Calculate(null, oxideRead).Valid, "First sample establishes baseline, not a fabricated rate");
Check(!Calculate(baseline, reader.Read(new CarbonCounter(), 5)).Valid, "Replacement plugin instance resets baseline");
carbon.TotalHookFires = 0;
Check(!Calculate(baseline, reader.Read(carbon, 5)).Valid, "Counter reset does not create a negative spike");
Check(!Calculate(baseline, reader.Read(carbon, 1)).Valid, "Zero elapsed interval is rejected");
oxide.TotalHookTime = double.NaN;
try { new CounterReader(oxide.GetType()).Read(oxide, 1); throw new Exception("NaN accepted"); }
catch (InvalidOperationException) { Check(true, "Non-finite framework counter rejected"); }
Check(EscapeTag("a b,c=d\\e\n") == "a\\ b\\,c\\=d\\\\e\\ ", "Tag escaping prevents line protocol injection");
Check(Quote("say \"hi\"\\\n") == "\"say \\\"hi\\\"\\\\ \"", "Event string escaping prevents line protocol injection");
Check(Quote(new string('a', 2000)).Length == 1002, "Event messages bounded");
var formatted = FormatLog("InvalidOperationException: bad input\n", "UnityEngine.Debug:LogException(Exception)\nOxide.Plugins.Example:Run(Arg) (at C:/private/source/Example.cs:42)\nSystem.Reflection.MethodBase:Invoke(Object,Object[])", "");
Check(formatted.Frames.Length == 1 && formatted.Source == "Example.Run(Arg) · Example.cs:42",
    "Stack formatting removes framework noise and shortens paths to file:line");
using (var detail = System.Text.Json.JsonDocument.Parse(formatted.Details))
    Check(detail.RootElement.GetProperty("frames").GetArrayLength() == 1 && detail.RootElement.GetProperty("omitted_frames").GetInt32() == 2,
        "Structured log details remain valid JSON with omission accounting");
var frameworkError = FormatLog("Failed to call hook Example\nSystem.InvalidOperationException: bad input\nat Oxide.Plugins.Example.Run() in C:/private/source/Example.cs:line 7", "", "");
using (var detail = System.Text.Json.JsonDocument.Parse(frameworkError.Details))
    Check(frameworkError.Message.Contains("InvalidOperationException: bad input") &&
        detail.RootElement.GetProperty("exception").GetString() == "System.InvalidOperationException" &&
        frameworkError.Frames.Length == 1 && !frameworkError.Source.Contains("private"),
        "Framework error messages preserve embedded exception and separate compact frames");
var multilineLog = FormatLog("Rust+ unavailable\nCould not establish TCP connection to 192.0.2.1:28083", "", "");
Check(multilineLog.Source == "" && multilineLog.Frames.Length == 0 && multilineLog.Message.Contains("TCP connection to [ip]"),
    "Multiline message context stays in the message instead of becoming a false stack frame");
Check(!Redact("{\"password\": \"secret-json\"} rcon.password secret-command", "").Contains("secret-"),
    "Quoted JSON credentials and space-separated console credentials redacted");
foreach (var endpoint in new[] { "https://127.0.0.1", "http://remote.example", "http://user:secret@127.0.0.1", "http://127.0.0.1/write" })
{
    try { Validate(new Settings { Endpoint = endpoint, Password = "test" }); throw new Exception("Unsafe endpoint accepted"); }
    catch (ArgumentException) { Check(true, "Invalid endpoint rejected: " + endpoint.Split('@').Last()); }
}

var adapter = new ServerMetricsAdapter();
adapter.Config.Value = new Settings { Password = "test", QueueLimit = 100 };
Invoke(adapter, "LoadConfig");
Invoke(adapter, "OnServerInitialized");
Check(InvokeProfiler.update.mode == 2 && InvokeProfiler.fixedUpdate.mode == 2 && Network.PacketProfiler.enabled,
    "Only required native detail counters enabled");
var nativeQueue = new ObjectWorkQueue { Name = "TestQueue", TotalExecutionTime = TimeSpan.FromMilliseconds(10) };
ObjectWorkQueue.All.Add(nativeQueue);
Invoke(adapter, "PollNativeDetails", 1000L);
nativeQueue.TotalExecutionTime = TimeSpan.FromMilliseconds(35);
Invoke(adapter, "PollNativeDetails", 2000L);
Check(((Queue<string>)Field(adapter, "_queue")).Any(line => line.StartsWith("work_queue") && line.Contains("duration=25 ")),
    "Native cumulative queue timing emits elapsed delta, not a repeated gauge");
nativeQueue.TotalExecutionTime = TimeSpan.Zero;
var queuedBeforeReset = ((Queue<string>)Field(adapter, "_queue")).Count;
Invoke(adapter, "PollNativeDetails", 3000L);
Check(((Queue<string>)Field(adapter, "_queue")).Count == queuedBeforeReset + 1,
    "Native queue reset omits negative duration (only packet sample emitted)");
var sameNameQueue = new ObjectWorkQueue { Name = "TestQueue", TotalExecutionTime = TimeSpan.FromMilliseconds(5) };
ObjectWorkQueue.All.Add(sameNameQueue);
Invoke(adapter, "PollNativeDetails", 4000L);
nativeQueue.TotalExecutionTime = TimeSpan.FromMilliseconds(10);
sameNameQueue.TotalExecutionTime = TimeSpan.FromMilliseconds(20);
var nativePoints = (Queue<string>)Field(adapter, "_queue");
nativePoints.Clear();
Performance.report = new Performance.Tick { frameID = 123, frameRate = 200, memoryUsageSystem = 1000, memoryAllocations = 400, gcTriggered = true };
Invoke(adapter, "PollServer", 6000L);
var beforeSameReport = nativePoints.Count;
Invoke(adapter, "PollServer", 7000L);
Check(nativePoints.Any(line => line.StartsWith("memory") && line.Contains("gc=true ")) && nativePoints.Count == beforeSameReport,
    "Native memory GC preserves Influx boolean schema and duplicate native reports are skipped");
nativePoints.Clear();
Invoke(adapter, "PollNativeDetails", 5000L);
Check(nativePoints.Count(line => line.StartsWith("work_queue")) == 1 &&
    nativePoints.Any(line => line.StartsWith("work_queue") && line.Contains("duration=25 ")),
    "Same-tag native duration deltas aggregate into one point without Influx overwrites");
nativePoints.Clear();
Check(UnityEngine.Application.Listeners == 1, "One threaded Unity log subscription includes main-thread logs");
var secretLog = "Player 76561190000000000 from 192.0.2.1:28015 and 2001:db8::1 Password=synthetic Bearer abcdef actual-secret";
Check(!Redact(secretLog, "actual-secret").Contains("76561190000000000") && !Redact(secretLog, "actual-secret").Contains("192.0.2.1") &&
    !Redact(secretLog, "actual-secret").Contains("2001:db8") && !Redact(secretLog, "actual-secret").Contains("synthetic") &&
    !Redact(secretLog, "actual-secret").Contains("abcdef") && !Redact(secretLog, "actual-secret").Contains("actual-secret"),
    "Player IDs, IPv4/IPv6 and common credentials redacted before queuing");
Parallel.For(0, 300, _ => UnityEngine.Application.Emit(secretLog, "bounded stack", UnityEngine.LogType.Error));
Invoke(adapter, "DrainLogs");
Check((long)Field(adapter, "_logsDropped") == 240, "Concurrent logging burst bounded by per-minute quota");
var stored = (Queue<string>)Field(adapter, "_queue");
Check(stored.Count(line => line.StartsWith("server_logs")) == 60 && !stored.Any(line => line.Contains("synthetic")),
    "Framework error severity captured and raw credentials never reach transport queue");
var countBefore = stored.Count;
UnityEngine.Application.Emit("[Server Metrics Adapter] failed upload", "", UnityEngine.LogType.Error);
UnityEngine.Application.Emit("Routine info message", "", UnityEngine.LogType.Log);
Invoke(adapter, "DrainLogs");
Check(stored.Count == countBefore, "Own diagnostics and default info logs excluded to avoid recursion/noise");
var queue = (Queue<string>)Field(adapter, "_queue");
queue.Clear();
for (var index = 0; index < 200; index++)
    Invoke(adapter, "Event", "operator_note", "Test", "bounded", "");
Check(queue.Count == 100 && (long)Field(adapter, "_dropped") == 100, "Outage backlog bounded with explicit drop accounting");
var timestamps = queue.Select(line => long.Parse(line.Split(' ').Last())).ToArray();
Check(timestamps.Distinct().Count() == timestamps.Length, "Same-series events cannot overwrite at the same millisecond");
Invoke(adapter, "Flush");
Invoke(adapter, "Flush");
Check(adapter.webrequest.Enqueued == 1 && System.Text.Encoding.UTF8.GetByteCount(adapter.webrequest.Body) <= 64000,
    "One asynchronous request at a time and bounded UTF-8 batch");
for (var attempt = 1; attempt <= 3; attempt++)
{
    adapter.webrequest.Callback(503, "unavailable");
    if (attempt < 3) { Set(adapter, "_retryAfter", 0d); Invoke(adapter, "Flush"); }
}
Check(adapter.webrequest.Enqueued == 3 && Field(adapter, "_batch") == null && (long)Field(adapter, "_failures") == 3,
    "Transient errors retry at most three times");
Set(adapter, "_retryAfter", 0d);
Invoke(adapter, "Event", "operator_note", "Test", "auth failure", "");
Invoke(adapter, "Flush");
adapter.webrequest.Callback(401, "unauthorized");
Check(Field(adapter, "_batch") == null, "Permanent authorization failure drops batch without retry loop");
Set(adapter, "_retryAfter", 0d);
Invoke(adapter, "Event", "operator_note", "Test", "unload", "");
Invoke(adapter, "Flush");
var staleCallback = adapter.webrequest.Callback;
Invoke(adapter, "Unload");
Check(UnityEngine.Application.Listeners == 0, "Unload removes exact log listener");
Check(InvokeProfiler.update.mode == 0 && InvokeProfiler.fixedUpdate.mode == 0 && !Network.PacketProfiler.enabled,
    "Unload restores owned native profiler flags");
var failuresBefore = (long)Field(adapter, "_failures");
staleCallback(503, "late callback");
Check(!(bool)Field(adapter, "_running") && queue.Count == 0 && (long)Field(adapter, "_failures") == failuresBefore,
    "Late upload callback after unload cannot resume old collector");
Console.WriteLine("All adapter behavioral checks passed.");

namespace AdapterTests
{
    public sealed class CarbonCounter
    {
        public TimeSpan TotalHookTime;
        public int TotalHookFires;
        public int TotalHookExceptions;
        public int TotalHookLagSpikes;
        public bool IsCorePlugin;
    }
    public sealed class OxideCounter
    {
        public double TotalHookTime { get; set; }
    }
}
