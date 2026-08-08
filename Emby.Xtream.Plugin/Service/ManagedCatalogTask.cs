using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    public class ManagedCatalogTask : IScheduledTask
    {
        private readonly ILogger _logger;

        public ManagedCatalogTask(ILogManager logManager)
        {
            _logger = logManager.GetLogger("XtreamTuner.ManagedCatalogTask");
        }

        public string Name => "m3u-editor for Emby - Reconcile Managed Libraries";
        public string Description => "Publishes the advertised version 1 managed catalog when the backend supports it.";
        public string Category => "Xtream Tuner";
        public string Key => "XtreamTunerManagedReconcile";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(2.5).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var plugin = Plugin.Instance;
            var config = plugin.Configuration;
            if (string.IsNullOrWhiteSpace(config.BaseUrl) ||
                string.IsNullOrWhiteSpace(config.Username) ||
                string.IsNullOrWhiteSpace(config.Password))
            {
                _logger.Info("Managed catalog reconcile skipped because Xtream credentials are not configured.");
                progress?.Report(100);
                return;
            }

            await plugin.StrmSyncService.ReconcileManagedAsync(
                config,
                () => plugin.SaveConfiguration(),
                progress,
                cancellationToken,
                () => plugin.ApplicationHost.Resolve<ILibraryManager>().QueueLibraryScan())
                .ConfigureAwait(false);
        }
    }
}
