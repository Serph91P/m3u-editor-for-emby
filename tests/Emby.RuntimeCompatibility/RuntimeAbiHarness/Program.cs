using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;

namespace Emby.RuntimeCompatibility
{
    internal static class Program
    {
        private const string PluginTypeName = "Emby.M3uEditor.Plugin.Service.M3uEditorLiveStream";
        private const string LiveStreamTypeName = "MediaBrowser.Controller.Library.ILiveStream";

        private static int Main(string[] args)
        {
            var runtimeVersion = Environment.Version;
            if (runtimeVersion.Major != 8 || runtimeVersion.Minor != 0 || runtimeVersion.Build != 28)
            {
                Console.Error.WriteLine("Runtime ABI harness requires .NET 8.0.28 but is executing on " + runtimeVersion + ".");
                return 1;
            }

            Console.WriteLine("Runtime ABI harness executing on .NET " + runtimeVersion + ".");

            if (args.Length != 5)
            {
                Console.Error.WriteLine("Usage: RuntimeAbiHarness <candidate> <released> <sdk> <server-4.9> <server-4.10>");
                return 2;
            }

            try
            {
                VerifyCandidate(args[0], "SDK 4.8.0.80", args[2]);
                VerifyCandidate(args[0], "Emby Server 4.9.5.0", args[3]);
                VerifyCandidate(args[0], "Emby Server 4.10.0.40", args[4]);
                VerifyHistoricalFailure(args[1], args[4]);
                Console.WriteLine("Runtime ABI compatibility checks passed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void VerifyCandidate(string pluginPath, string runtimeName, string runtimeDirectory)
        {
            using (var context = new ServerAssemblyLoadContext(runtimeDirectory, Path.GetDirectoryName(pluginPath)))
            {
                var controller = context.LoadFromAssemblyPath(Path.Combine(runtimeDirectory, "MediaBrowser.Controller.dll"));
                VerifyPlaybackLifecycleContract(controller, runtimeName);
                var liveStreamInterface = controller.GetType(LiveStreamTypeName, true);
                if (!File.Exists(Path.Combine(runtimeDirectory, "System.IO.Pipelines.dll")))
                    context.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "System.IO.Pipelines.dll"));
                var plugin = context.LoadFromAssemblyPath(pluginPath);
                var liveStreamType = plugin.GetType(PluginTypeName, true);
                var map = liveStreamType.GetInterfaceMap(liveStreamInterface);

                if (map.InterfaceMethods.Length != liveStreamInterface.GetMethods().Length ||
                    map.TargetMethods.Length != map.InterfaceMethods.Length ||
                    map.TargetMethods.Any(method => method == null))
                    throw new InvalidOperationException(runtimeName + " did not map every ILiveStream member.");

                var instance = CreateLiveStream(context, liveStreamType);
                try
                {
                    InvokeConsumer(map, liveStreamType, instance, "AddConsumer");
                    AssertConsumerCount(liveStreamType, instance, 1, runtimeName + " after AddConsumer");
                    InvokeConsumer(map, liveStreamType, instance, "RemoveConsumer");
                    AssertConsumerCount(liveStreamType, instance, 0, runtimeName + " after RemoveConsumer");
                    Console.WriteLine(runtimeName + ": candidate mapped " + map.InterfaceMethods.Length + " ILiveStream members.");
                }
                finally
                {
                    (instance as IDisposable)?.Dispose();
                }
            }
        }

        private static void VerifyPlaybackLifecycleContract(Assembly controller, string runtimeName)
        {
            var sessionManager = controller.GetType("MediaBrowser.Controller.Session.ISessionManager", true);
            var userDataManager = controller.GetType("MediaBrowser.Controller.Library.IUserDataManager", true);
            var entryPoint = controller.GetType("MediaBrowser.Controller.Plugins.IServerEntryPoint", true);

            var progress = AssertEvent(sessionManager, "PlaybackStart", "MediaBrowser.Controller.Library.PlaybackProgressEventArgs", runtimeName);
            AssertEvent(sessionManager, "PlaybackProgress", "MediaBrowser.Controller.Library.PlaybackProgressEventArgs", runtimeName);
            var stopped = AssertEvent(sessionManager, "PlaybackStopped", "MediaBrowser.Controller.Library.PlaybackStopEventArgs", runtimeName);
            var userDataSaved = AssertEvent(userDataManager, "UserDataSaved", "MediaBrowser.Controller.Library.UserDataSaveEventArgs", runtimeName);

            AssertMember(progress, "Item", runtimeName);
            AssertMember(progress, "MediaSourceId", runtimeName);
            AssertMember(progress, "PlaySessionId", runtimeName);
            var positionTicksType = AssertMember(progress, "PlaybackPositionTicks", runtimeName);
            if (positionTicksType != typeof(long?))
                throw new InvalidOperationException(runtimeName + " PlaybackProgressEventArgs.PlaybackPositionTicks must be nullable Int64.");
            AssertMember(progress, "Session", runtimeName);
            AssertMember(progress, "Users", runtimeName);
            AssertMember(stopped, "PlayedToCompletion", runtimeName);
            AssertMember(userDataSaved, "Item", runtimeName);
            AssertMember(userDataSaved, "User", runtimeName);
            AssertMember(userDataSaved, "UserData", runtimeName);
            AssertMember(userDataSaved, "SaveReason", runtimeName);

            var run = entryPoint.GetMethod("Run", Type.EmptyTypes);
            if (run == null || run.ReturnType != typeof(void))
                throw new InvalidOperationException(runtimeName + " IServerEntryPoint.Run(): void is required.");
        }

        private static Type AssertEvent(Type owner, string name, string argumentTypeName, string runtimeName)
        {
            var eventInfo = owner.GetEvent(name);
            var handlerType = eventInfo == null ? null : eventInfo.EventHandlerType;
            if (handlerType == null || !handlerType.IsGenericType ||
                handlerType.GetGenericTypeDefinition() != typeof(EventHandler<>) ||
                handlerType.GetGenericArguments()[0].FullName != argumentTypeName)
                throw new InvalidOperationException(runtimeName + " requires " + owner.FullName + "." + name + " as EventHandler<" + argumentTypeName + ">.");
            return handlerType.GetGenericArguments()[0];
        }

        private static Type AssertMember(Type owner, string name, string runtimeName)
        {
            var property = owner.GetProperty(name);
            if (property != null)
                return property.PropertyType;
            var field = owner.GetField(name);
            if (field != null)
                return field.FieldType;
            throw new InvalidOperationException(runtimeName + " requires " + owner.FullName + "." + name + "; available members: " + string.Join(",", owner.GetMembers().Select(member => member.Name).Distinct().OrderBy(member => member)) + ".");
        }

        private static void VerifyHistoricalFailure(string releasedPluginPath, string runtimeDirectory)
        {
            try
            {
                using (var context = new ServerAssemblyLoadContext(runtimeDirectory, Path.GetDirectoryName(releasedPluginPath)))
                {
                    var controller = context.LoadFromAssemblyPath(Path.Combine(runtimeDirectory, "MediaBrowser.Controller.dll"));
                    var liveStreamInterface = controller.GetType(LiveStreamTypeName, true);
                    var plugin = context.LoadFromAssemblyPath(releasedPluginPath);
                    var liveStreamType = plugin.GetType(PluginTypeName, true);
                    liveStreamType.GetInterfaceMap(liveStreamInterface);
                }
            }
            catch (TypeLoadException exception) when (exception.Message.IndexOf("AddConsumer", StringComparison.Ordinal) >= 0)
            {
                Console.WriteLine("Emby Server 4.10.0.40: released v1.5.0 failed as expected: " + exception.Message);
                return;
            }

            throw new InvalidOperationException("Released v1.5.0 unexpectedly mapped to Emby Server 4.10.0.40.");
        }

        private static object CreateLiveStream(ServerAssemblyLoadContext context, Type liveStreamType)
        {
            var mediaSourceType = context.LoadFromAssemblyPath(Path.Combine(context.RuntimeDirectory, "MediaBrowser.Model.dll"))
                .GetType("MediaBrowser.Model.Dto.MediaSourceInfo", true);
            var mediaSource = Activator.CreateInstance(mediaSourceType);
            mediaSourceType.GetProperty("Id").SetValue(mediaSource, "runtime-abi");
            mediaSourceType.GetProperty("Path").SetValue(mediaSource, "http://127.0.0.1:9/deferred");

            var constructor = liveStreamType.GetConstructors().Single(ctor => ctor.GetParameters().Length == 4);
            return constructor.Invoke(new object[] { mediaSource, "runtime-abi", new HttpClient(), null });
        }

        private static void InvokeConsumer(InterfaceMapping map, Type liveStreamType, object instance, string name)
        {
            var index = Array.FindIndex(map.InterfaceMethods, method => method.Name == name);
            if (index >= 0)
            {
                map.InterfaceMethods[index].Invoke(instance, new object[] { "runtime-abi" });
                return;
            }

            // 4.8 and 4.9 predate the interface callbacks; the candidate method
            // still executes in their isolated runtime contexts.
            liveStreamType.GetMethod(name, new[] { typeof(string) }).Invoke(instance, new object[] { "runtime-abi" });
        }

        private static void AssertConsumerCount(Type liveStreamType, object instance, int expected, string message)
        {
            var actual = (int)liveStreamType.GetProperty("ConsumerCount").GetValue(instance);
            if (actual != expected)
                throw new InvalidOperationException(message + ": expected " + expected + ", actual " + actual + ".");
        }
    }

    internal sealed class ServerAssemblyLoadContext : AssemblyLoadContext, IDisposable
    {
        private readonly string _pluginDirectory;

        public ServerAssemblyLoadContext(string runtimeDirectory, string pluginDirectory)
            : base("emby-runtime-" + Path.GetFileName(runtimeDirectory), true)
        {
            RuntimeDirectory = runtimeDirectory;
            _pluginDirectory = pluginDirectory;
        }

        public string RuntimeDirectory { get; }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == "System.IO.Pipelines")
            {
                var pipelinesAssembly = Path.Combine(RuntimeDirectory, assemblyName.Name + ".dll");
                if (File.Exists(pipelinesAssembly))
                    return LoadFromAssemblyPath(pipelinesAssembly);
            }

            if (assemblyName.Name == "netstandard" || assemblyName.Name.StartsWith("System", StringComparison.Ordinal) || assemblyName.Name.StartsWith("Microsoft", StringComparison.Ordinal))
                return null;

            var runtimeAssembly = Path.Combine(RuntimeDirectory, assemblyName.Name + ".dll");
            if (File.Exists(runtimeAssembly))
                return LoadFromAssemblyPath(runtimeAssembly);

            var pluginAssembly = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
            if (File.Exists(pluginAssembly))
                return LoadFromAssemblyPath(pluginAssembly);

            return null;
        }

        public void Dispose()
        {
            Unload();
        }
    }
}
