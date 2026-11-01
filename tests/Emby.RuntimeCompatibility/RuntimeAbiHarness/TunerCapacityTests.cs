using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.RuntimeCompatibility
{
    internal sealed partial class RegistryLifecycleTests
    {
        private void AssertNativeCapacity(ServerAssemblyLoadContext context, Assembly plugin, bool full)
        {
            var controller = context.LoadFromAssemblyPath(Path.Combine(context.RuntimeDirectory, "MediaBrowser.Controller.dll"));
            var model = context.LoadFromAssemblyPath(Path.Combine(context.RuntimeDirectory, "MediaBrowser.Model.dll"));
            var baseHost = controller.GetType("MediaBrowser.Controller.LiveTv.BaseTunerHost", true);
            var tuner = Activator.CreateInstance(model.GetType("MediaBrowser.Model.LiveTv.TunerHostInfo", true));
            NativePlaybackFixture.Set(tuner, "Id", "synthetic-tuner");
            NativePlaybackFixture.Set(tuner, "TunerCount", 3);
            var method = baseHost.GetMethods().Single(m => m.Name == "GetChannelStream" && m.GetParameters().Length == 6);
            var streams = Activator.CreateInstance(method.GetParameters()[4].ParameterType);
            var registered = (IDictionary)_managerType.GetField("_openStreams", Instance).GetValue(_manager);
            foreach (var value in registered.Values) streams.GetType().GetMethod("Add").Invoke(streams, new[] { value });
            if (!full)
            {
                // Only channel discovery is prefilled; admission and the plugin's deferred open are real.
                var channelType = controller.GetType("MediaBrowser.Controller.LiveTv.ChannelInfo", true);
                var channel = Activator.CreateInstance(channelType);
                NativePlaybackFixture.Set(channel, "Id", "44");
                NativePlaybackFixture.Set(channel, "Number", "44");
                var cacheField = baseHost.GetField("_channelCache", Instance);
                var cache = Activator.CreateInstance(cacheField.FieldType);
                var entry = Activator.CreateInstance(cacheField.FieldType.GetGenericArguments()[1], true);
                var channels = Activator.CreateInstance(typeof(List<>).MakeGenericType(channelType));
                channels.GetType().GetMethod("Add").Invoke(channels, new[] { channel });
                entry.GetType().GetField("Channels").SetValue(entry, channels);
                cache.GetType().GetMethod("TryAdd").Invoke(cache, new object[] { "synthetic-tuner", entry });
                cacheField.SetValue(_host, cache);
                var stats = _host.GetType().GetField("_streamStats", Instance);
                stats.SetValue(_host, Activator.CreateInstance(stats.FieldType));
                NativePlaybackFixture.Set(_host, "Logger", DispatchProxy.Create(model.GetType("MediaBrowser.Model.Logging.ILogger", true), typeof(OfflineLogger)));
                var config = plugin.GetType("Emby.M3uEditor.Plugin.Plugin", true).GetProperty("Instance").GetValue(null);
                config = config.GetType().GetProperty("Configuration").GetValue(config);
                NativePlaybackFixture.Set(config, "BaseUrl", "https://synthetic.example.test");
                NativePlaybackFixture.Set(config, "Username", "synthetic");
                NativePlaybackFixture.Set(config, "Password", "synthetic");
            }
            try
            {
                var task = (Task)method.Invoke(_host, new object[] { tuner, null, "44", null, streams, CancellationToken.None });
                task.GetAwaiter().GetResult();
                Require(!full, "Native tuner admitted a fourth stream at limit three");
                var opened = NativePlaybackFixture.Get(task, "Result");
                Require(opened != null, "Native freed tuner slot could not open another channel");
                ((IDisposable)opened).Dispose();
                Console.WriteLine("Native BaseTunerHost: original TunerCount=3 reusable after final stop.");
            }
            catch (Exception exception) when (full && exception.GetBaseException().GetType().Name == "LiveTvConflictException")
            {
                Console.WriteLine("Native BaseTunerHost: fourth open rejected at original TunerCount=3.");
            }
        }
    }
}
