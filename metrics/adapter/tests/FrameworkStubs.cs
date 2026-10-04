// Controllable framework boundary for outage/unload tests. Real API compatibility
// is checked separately against the installed Carbon and saved Oxide assemblies.
namespace Oxide.Core.Plugins
{
    public class Plugin
    {
        public bool IsLoaded = true;
        public string Name = "test";
        public string Title = "Test";
    }
}

namespace Oxide.Core.Libraries
{
    public enum RequestMethod { POST }
}

namespace Oxide.Plugins
{
    [AttributeUsage(AttributeTargets.Class)]
    public class InfoAttribute : Attribute
    {
        public InfoAttribute(string name, string author, string version) { }
    }
    [AttributeUsage(AttributeTargets.Class)]
    public class DescriptionAttribute : Attribute
    {
        public DescriptionAttribute(string description) { }
    }
    [AttributeUsage(AttributeTargets.Method)]
    public class ConsoleCommandAttribute : Attribute
    {
        public ConsoleCommandAttribute(string name) { }
    }
    public class RustPlugin : Oxide.Core.Plugins.Plugin
    {
        public FakeConfig Config = new();
        public FakePlugins plugins = new();
        public FakeTimers timer = new();
        public FakeRequests webrequest = new();
        public List<string> Warnings = [];
        protected virtual void LoadConfig() { }
        protected virtual void LoadDefaultConfig() { }
        public void Puts(string text) { }
        public void PrintWarning(string text) { Warnings.Add(text); }
        public void NextTick(Action callback) { callback(); }
    }
    public class FakeConfig
    {
        public object Value;
        public bool Present = true;
        public Exception ReadFailure;
        public bool Exists() { return Present; }
        public T ReadObject<T>()
        {
            if (ReadFailure != null) throw ReadFailure;
            return (T)Value;
        }
        public void WriteObject<T>(T value, bool pretty) { Value = value; }
    }
    public class FakePlugins
    {
        public Oxide.Core.Plugins.Plugin[] Values = [];
        public Oxide.Core.Plugins.Plugin[] GetAll() { return Values; }
    }
    public class FakeTimers
    {
        public void Every(float seconds, Action callback) { }
    }
    public class FakeRequests
    {
        public int Enqueued;
        public string Body;
        public Action<int, string> Callback;
        public void Enqueue(string url, string body, Action<int, string> callback, Oxide.Core.Plugins.Plugin owner,
            Oxide.Core.Libraries.RequestMethod method, Dictionary<string, string> headers, float timeout)
        {
            Enqueued++;
            Body = body;
            Callback = callback;
        }
    }
}

public static class ConsoleSystem
{
    public class Arg
    {
        public object Connection;
        public string[] Args;
        public void ReplyWith(string text) { }
    }
}
public static class Performance
{
    public static Tick report;
    public struct Tick
    {
        public int frameRate;
        public float frameTime;
        public long memoryUsageSystem;
        public int memoryCollections;
        public long memoryAllocations;
        public bool gcTriggered;
        public int loadBalancerTasks;
        public int invokeHandlerTasks;
        public int workshopSkinsQueued;
        public int frameID;
        public PerformanceSamplePoint performanceSample;
    }
}
public class BasePlayer
{
    public Network.Networkable net;
    public static readonly List<BasePlayer> activePlayerList = [];
}

public static class BaseNetworkable
{
    public static readonly List<object> serverEntities = [];
}
public class ServerMgr
{
    public static ServerMgr Instance;
    public ConnectionQueue connectionQueue;
}
public class ConnectionQueue
{
    public int Joining;
    public int Queued;
}
namespace Network
{
    public static class Net { public static BaseNetwork sv; }
    public class BaseNetwork
    {
        public enum StatTypeLong { BytesReceived_LastSecond, BytesSent_LastSecond, PacketLossLastSecond }
        public long GetStat(object connection, StatTypeLong type) { return 0; }
        public long GetAveragePing(object connection) { return 0; }
    }
    public class Networkable { public Connection connection; }
    public class Connection { public ulong userid; }
    public static class PacketProfiler
    {
        public static bool enabled;
        public static int[] outboundSum = new int[30];
        public static int[] outboundBytes = new int[30];
    }
    public class Message { public enum Type { First, Welcome, Count = 29 } }
}

public struct PerformanceSamplePoint
{
    public TimeSpan Update, LateUpdate, FixedUpdate, PhysicsUpdate;
}
public class InvokeProfiler
{
    public static InvokeProfiler update = new();
    public static InvokeProfiler fixedUpdate = new();
    public int mode;
    public List<InvokeTrackingData> trackingDataList = [];
}
public class InvokeTrackingData
{
    public InvokeTrackingKey Key;
    public string TypeName;
    public TimeSpan ExecutionTime;
}
public struct InvokeTrackingKey
{
    public Type Type;
    public string MethodName;
}
public class ObjectWorkQueue
{
    public static List<ObjectWorkQueue> All = [];
    public string Name;
    public TimeSpan TotalExecutionTime;
}
public class PersistentObjectWorkQueue
{
    public static List<PersistentObjectWorkQueue> All = [];
    public string Name;
    public TimeSpan TotalExecutionTime;
}

namespace UnityEngine
{
    public enum LogType { Error, Assert, Warning, Log, Exception }
    public static class Application
    {
        public delegate void LogCallback(string message, string stack, LogType type);
        public static event LogCallback logMessageReceivedThreaded;
        public static int Listeners => logMessageReceivedThreaded?.GetInvocationList().Length ?? 0;
        public static void Emit(string message, string stack, LogType type)
        {
            logMessageReceivedThreaded?.Invoke(message, stack, type);
        }
    }
}
