using System;
using System.Collections;
using System.Collections.Generic;
using RustServerMetrics.Config;

namespace RustServerMetrics
{
    internal class MetricsLogger
    {
        public bool Ready;
        public ConfigData Configuration;
        public Uri BaseUri => InfluxConnection.Create(Configuration).WriteUri;
        public string AuthorizationHeader => InfluxConnection.Create(Configuration).AuthorizationHeader;
    }
}

namespace UnityEngine
{
    internal class Behaviour { }
    internal class MonoBehaviour : Behaviour
    {
        public RustServerMetrics.MetricsLogger Logger;
        public IEnumerator Routine;
        protected T GetComponent<T>() where T : class => Logger as T;
        protected void Destroy(object target) => throw new InvalidOperationException("Unexpected destroy");
        protected void StartCoroutine(IEnumerator routine) => Routine = routine;
        protected void StopAllCoroutines() => (Routine as IDisposable)?.Dispose();

        public static void Drain(IEnumerator routine)
        {
            while (routine.MoveNext())
                if (routine.Current is IEnumerator nested) Drain(nested);
            (routine as IDisposable)?.Dispose();
        }
    }
    internal class WaitForSecondsRealtime(float seconds) { public float Seconds = seconds; }
    internal static class Debug { public static void LogError(object value) { } }
}

internal static class InvokeHandler
{
    public static void Invoke(UnityEngine.Behaviour sender, Action callback, float delay) { }
    public static void CancelInvoke(UnityEngine.Behaviour sender, Action callback) { }
}

namespace UnityEngine.Networking
{
    internal class UploadHandlerRaw(byte[] data) { public byte[] Data = data; }
    internal class DownloadHandlerBuffer { public string text = "request failed"; }
    internal class UnityWebRequest : IDisposable
    {
        public enum Result { Success, ConnectionError, ProtocolError }
        public const string kHttpVerbPOST = "POST";
        public static readonly Queue<(long, Result)> Results = new();
        public static readonly List<UnityWebRequest> Created = new();
        public readonly Uri Uri;
        public readonly Dictionary<string, string> Headers = new();
        public UploadHandlerRaw uploadHandler;
        public DownloadHandlerBuffer downloadHandler;
        public int timeout;
        public int redirectLimit;
        public Result result;
        public long responseCode;
        public string error = "request failed";
        public bool Disposed;
        public byte[] Data => uploadHandler.Data;
        public UnityWebRequest(Uri uri, string method) { Uri = uri; Created.Add(this); }
        public void SetRequestHeader(string key, string value) => Headers[key] = value;
        public object SendWebRequest() { (responseCode, result) = Results.Dequeue(); return null; }
        public void Abort() { }
        public void Dispose() => Disposed = true;
    }
}
