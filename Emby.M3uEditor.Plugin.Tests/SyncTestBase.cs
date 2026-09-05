using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using Emby.M3uEditor.Plugin.Service;
using Emby.M3uEditor.Plugin.Tests.Fakes;
using MediaBrowser.Model.Logging;

namespace Emby.M3uEditor.Plugin.Tests
{
    public abstract class SyncTestBase : IDisposable
    {
        protected readonly FakeHttpHandler Handler;
        protected readonly HttpClient HttpClient;
        protected readonly TempDirectory TempDir;
        protected int SaveConfigCallCount;

        protected SyncTestBase()
        {
            Handler = new FakeHttpHandler();
            HttpClient = new HttpClient(Handler);
            TempDir = new TempDirectory();
            SaveConfigCallCount = 0;
        }

        protected Action SaveConfig => () => SaveConfigCallCount++;

        protected PluginConfiguration DefaultConfig() => new PluginConfiguration
        {
            BaseUrl               = "http://fake-xtream",
            Username              = "user",
            Password              = "pass",
            ManagedApprovedOutputRoots = TempDir.Path,
            ManagedPublishingIntegrationId = 7,
            ManagedSetupReady       = true,
            ManagedSetupLastResult  = "Ready",
        };

        protected StrmSyncService MakeService()
        {
            return new StrmSyncService(new NullLogger(), HttpClient);
        }

        protected StrmSyncService MakeService(HttpClient httpClient)
        {
            return new StrmSyncService(new NullLogger(), httpClient);
        }

        protected static readonly CancellationToken None = CancellationToken.None;

        public void Dispose()
        {
            HttpClient.Dispose();
            TempDir.Dispose();
        }

        private class NullLogger : ILogger
        {
            public void Info(string message, params object[] paramList) { }
            public void Error(string message, params object[] paramList) { }
            public void Warn(string message, params object[] paramList) { }
            public void Debug(string message, params object[] paramList) { }
            public void Fatal(string message, params object[] paramList) { }
            public void FatalException(string message, Exception exception, params object[] paramList) { }
            public void ErrorException(string message, Exception exception, params object[] paramList) { }
            public void LogMultiline(string message, LogSeverity severity, StringBuilder additionalContent) { }
            public void Log(LogSeverity severity, string message, params object[] paramList) { }
            public void Info(ReadOnlyMemory<char> message) { }
            public void Error(ReadOnlyMemory<char> message) { }
            public void Warn(ReadOnlyMemory<char> message) { }
            public void Debug(ReadOnlyMemory<char> message) { }
            public void Log(LogSeverity severity, ReadOnlyMemory<char> message) { }
        }
    }
}
