using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using RustServerMetrics;
using RustServerMetrics.Config;
using UnityEngine;
using UnityEngine.Networking;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}

ConfigData Valid() => new()
{
    Enabled = true,
    DatabaseUrl = "http://127.0.0.1:18086",
    DatabaseName = "rust & metrics/\u00e4",
    DatabaseUser = "writer",
    DatabasePassword = "p&?/#$:\u00e4",
    ServerTag = "local-dev"
};

var config = Valid();
var connection = InfluxConnection.Create(config);
Check(connection.WriteUri.Query == "?db=rust%20%26%20metrics%2F%C3%A4&precision=ms", "Database query escaping");
Check(!connection.WriteUri.AbsoluteUri.Contains(config.DatabasePassword), "Credentials excluded from URI");
Check(Encoding.UTF8.GetString(Convert.FromBase64String(connection.AuthorizationHeader[6..])) == "writer:p&?/#$:\u00e4", "Reserved and Unicode credentials");
config.DatabaseUrl = "https://example.org/influx";
Check(InfluxConnection.Create(config).WriteUri.AbsolutePath == "/influx/write", "Reverse proxy path preserved");

foreach (var invalid in new Action<ConfigData>[]
{
    c => c.DatabaseUrl = null,
    c => c.DatabaseUrl = "relative",
    c => c.DatabaseUrl = "ftp://example.org",
    c => c.DatabaseUrl = "http://user:pass@example.org",
    c => c.DatabaseUrl = "http://example.org?token=secret",
    c => c.DatabaseUrl = "http://example.org#fragment",
    c => c.DatabaseUrl = ConfigData.DefaultInfluxDbUrl,
    c => c.DatabaseName = null,
    c => c.DatabaseName = " ",
    c => c.DatabaseName = "db\nname",
    c => c.DatabaseUser = null,
    c => c.DatabaseUser = "user:name",
    c => c.DatabasePassword = null,
    c => c.DatabasePassword = "secret\r\nheader",
    c => c.ServerTag = null,
    c => c.ServerTag = "server tag",
    c => c.ServerTag = "server,tag",
    c => c.ServerTag = "server=tag",
    c => c.BatchSize = 0,
    c => c.BatchSize = 999
})
{
    config = Valid();
    invalid(config);
    try { InfluxConnection.Create(config); throw new Exception("Accepted invalid config"); }
    catch (ArgumentException) { checks++; }
}
Check(JsonConvert.DeserializeObject<ConfigData>("null") == null, "Null JSON boundary");
try { JsonConvert.DeserializeObject<ConfigData>("[]"); throw new Exception("Accepted array config"); }
catch (JsonSerializationException) { checks++; }

Check(MetricValues.NetworkBytes(1_500_000_000, 3) == 4_500_000_000L, "Wide multiplication");
Check(MetricValues.NetworkBytes(3_000_000_000L, 2) == 6_000_000_000L, "Wide input length");
foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
{
    var payload = new StringBuilder("memory used=1i\n");
    Check(!MetricValues.AppendFrameMetric(payload, "framerate", "local-dev", "123", invalid, 10), "Reject nonfinite instant: " + invalid);
    Check(!MetricValues.AppendFrameMetric(payload, "frametime", "local-dev", "123", 10, invalid), "Reject nonfinite average: " + invalid);
    Check(payload.ToString() == "memory used=1i\n", "Invalid frame readings preserve other metrics");
}
var previousCulture = CultureInfo.CurrentCulture;
try
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
    var payload = new StringBuilder();
    Check(MetricValues.AppendFrameMetric(payload, "frametime", "local-dev", "123", 4.5f, 0), "Finite readings including zero are accepted");
    Check(payload.ToString() == "frametime,server=local-dev instant=4.5,average=0 123\n", "Frame values use invariant line protocol");
}
finally
{
    CultureInfo.CurrentCulture = previousCulture;
}
foreach (var (address, expected) in new[]
{
    ("127.0.0.1:28015", "127.0.0.1"), ("127.0.0.1", "127.0.0.1"),
    ("[2001:db8::1]:28015", "2001:db8::1"), ("2001:db8::1", "2001:db8::1"),
    ("localhost", "localhost"), (null, ""), ("", "")
}) Check(MetricValues.AddressHost(address) == expected, "Address parsing: " + address);

var tags = new StringBuilder();
MetricValues.AppendConnectionTags(tags, "123", null);
Check(tags.ToString() == ",steamid=123", "Missing address never emits an empty ip tag");
tags.Clear();
MetricValues.AppendConnectionTags(tags, "123", "127.0.0.1:28015");
Check(tags.ToString() == ",steamid=123,ip=127.0.0.1", "Connection tags preserve host and SteamID");

foreach (var status in new long[] { 400, 401, 403, 404, 413, 204 })
    Check(!UploadPolicy.ShouldRetry(false, status, 1), "Permanent HTTP result: " + status);
foreach (var status in new long[] { 429, 500, 502, 503 })
{
    Check(UploadPolicy.ShouldRetry(false, status, 1), "Transient HTTP result: " + status);
    Check(!UploadPolicy.ShouldRetry(false, status, 3), "Bounded HTTP retry: " + status);
}
Check(UploadPolicy.ShouldRetry(true, 0, 2), "Network retry");
Check(!UploadPolicy.ShouldRetry(true, 0, 3), "Bounded network retry");
Check(UploadPolicy.ShouldCoalesce(999, 1000), "Small batch coalesces");
Check(!UploadPolicy.ShouldCoalesce(1000, 1000), "Full batch drains immediately");

ReportUploader Uploader()
{
    var uploader = new ReportUploader
    {
        Logger = new MetricsLogger { Configuration = Valid(), Ready = true }
    };
    typeof(ReportUploader).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(uploader, null);
    return uploader;
}

var queue = Uploader();
for (var i = 0; i <= UploadPolicy.BufferCapacity; i++) queue.AddToSendBuffer("point value=1");
Check(queue.BufferSize == UploadPolicy.BufferCapacity && queue.DroppedReports == 1, "Queue overflow drops oldest and counts loss");
queue.Stop();
Check(queue.BufferSize == 0 && queue.DroppedReports == UploadPolicy.BufferCapacity + 1, "Stop clears and accounts pending reports");
queue.Logger.Ready = false;
queue.AddToSendBuffer("point value=1");
Check(queue.BufferSize == 0 && !queue.IsRunning, "Disabled logger cannot restart uploader");

var upload = Uploader();
UnityWebRequest.Results.Enqueue((503, UnityWebRequest.Result.ProtocolError));
UnityWebRequest.Results.Enqueue((204, UnityWebRequest.Result.Success));
upload.AddToSendBuffer("point value=1");
MonoBehaviour.Drain(upload.Routine);
Check(upload.SentBatches == 1 && upload.FailedBatches == 0 && upload.DroppedReports == 0, "Transient failure retries same batch successfully");
Check(UnityWebRequest.Created.All(r => r.Disposed && r.Headers.ContainsKey("Authorization") && !r.Uri.Query.Contains("&p=")), "Every request disposed and credentials use header");
Check(UnityWebRequest.Created.All(r => Encoding.UTF8.GetString(r.Data) == "point value=1\n"), "Retry retains exact payload");

var failure = Uploader();
UnityWebRequest.Results.Enqueue((401, UnityWebRequest.Result.ProtocolError));
failure.AddToSendBuffer("point value=2");
MonoBehaviour.Drain(failure.Routine);
Check(failure.FailedBatches == 1 && failure.DroppedReports == 1 && !failure.IsRunning, "Permanent failure counts batch and dropped reports");

foreach (var (status, result, expectedDrops) in new[]
{
    (204L, UnityWebRequest.Result.Success, 0L),
    (401L, UnityWebRequest.Result.ProtocolError, 1L)
})
{
    var finishing = Uploader();
    UnityWebRequest.Results.Enqueue((status, result));
    finishing.AddToSendBuffer("point value=3");
    Check(finishing.Routine.MoveNext(), "Initial coalescing wait");
    Check(finishing.Routine.MoveNext(), "Nested upload available");
    MonoBehaviour.Drain((System.Collections.IEnumerator)finishing.Routine.Current);
    finishing.Stop();
    Check(finishing.DroppedReports == expectedDrops, "Stop after request completion counts loss once");
}
var interrupted = Uploader();
UnityWebRequest.Results.Enqueue((204, UnityWebRequest.Result.Success));
interrupted.AddToSendBuffer("point value=4");
interrupted.Routine.MoveNext();
interrupted.Routine.MoveNext();
var inFlight = (System.Collections.IEnumerator)interrupted.Routine.Current;
inFlight.MoveNext();
interrupted.Stop();
Check(interrupted.DroppedReports == 1 && UnityWebRequest.Created.Last().Disposed, "Stop disposes active request and counts its report");

var backingOff = Uploader();
UnityWebRequest.Results.Enqueue((503, UnityWebRequest.Result.ProtocolError));
backingOff.AddToSendBuffer("point value=5");
backingOff.Routine.MoveNext();
backingOff.Routine.MoveNext();
var retrying = (System.Collections.IEnumerator)backingOff.Routine.Current;
retrying.MoveNext();
retrying.MoveNext();
Check(retrying.Current is WaitForSecondsRealtime, "Transient failure backs off before retry");
backingOff.Stop();
Check(backingOff.DroppedReports == 1 && !backingOff.IsRunning, "Stop during retry counts pending report once");

Console.WriteLine($"PASS: {checks} regression checks (configuration, authentication, counters, queue, retry, and disposal)");
