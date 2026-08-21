using System;
using System.Collections.Concurrent;
using System.IO;
using Emby.M3uEditor.Plugin.Api;

namespace Emby.M3uEditor.Plugin.Service
{
    internal sealed class ManagedSetupService
    {
        internal const int ApiVersion = 1;
        private const string ManagedRootName = "managed-publishing";
        private static readonly ConcurrentDictionary<string, object> SetupLocks =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private readonly string _ownerPath;

        internal ManagedSetupService(string ownerPath)
        {
            _ownerPath = ownerPath;
        }

        internal ManagedSetupResult Get(PluginConfiguration config)
        {
            if (config == null || !config.ManagedSetupReady ||
                config.ManagedPublishingIntegrationId < 1)
            {
                return Failed("Managed setup is not ready.");
            }

            var roots = ManagedOutputPolicy.GetCanonicalRoots(config.ManagedApprovedOutputRoots);
            if (roots.Count != 1)
            {
                return Failed("Managed setup is not ready.");
            }

            return Ready(config.ManagedPublishingIntegrationId, roots[0]);
        }

        internal ManagedSetupResult Put(PluginConfiguration config, int integrationId, Action saveConfiguration)
        {
            if (config == null || integrationId < 1)
            {
                return Failed("A positive integration binding is required.");
            }

            string candidate;
            try
            {
                candidate = Path.Combine(_ownerPath ?? string.Empty, ManagedRootName);
            }
            catch (ArgumentException)
            {
                return Failed("The managed output root is not safe.");
            }

            var gate = SetupLocks.GetOrAdd(candidate, _ => new object());
            lock (gate)
            {
                if (config.ManagedPublishingIntegrationId > 0 &&
                    config.ManagedPublishingIntegrationId != integrationId)
                {
                    return Failed("The managed integration binding conflicts with the existing binding.");
                }

                string root;
                string validationError;
                if (!ManagedOutputPolicy.TryValidateSetupRoot(
                    _ownerPath,
                    candidate,
                    config.ManagedApprovedOutputRoots,
                    config.StrmLibraryPath,
                    config.SyncMovies || config.SyncSeries,
                    out root,
                    out validationError))
                {
                    return Failed(validationError);
                }

                try
                {
                    Directory.CreateDirectory(root);
                }
                catch (Exception ex) when (
                    ex is ArgumentException ||
                    ex is NotSupportedException ||
                    ex is IOException ||
                    ex is UnauthorizedAccessException)
                {
                    return Failed("The managed output root is not locally writable.");
                }

                if (!ManagedOutputPolicy.IsLocallyWritableRoot(root))
                {
                    return Failed("The managed output root is not locally writable.");
                }

                if (config.ManagedSetupReady &&
                    config.ManagedPublishingIntegrationId == integrationId &&
                    string.Equals(config.ManagedApprovedOutputRoots, root, PathComparison) &&
                    config.ManagedPublishingApiVersion == ApiVersion)
                {
                    return Ready(integrationId, root);
                }

                var oldIntegrationId = config.ManagedPublishingIntegrationId;
                var oldApprovedRoots = config.ManagedApprovedOutputRoots;
                var oldReady = config.ManagedSetupReady;
                var oldLastResult = config.ManagedSetupLastResult;
                var oldApiVersion = config.ManagedPublishingApiVersion;
                config.ManagedPublishingIntegrationId = integrationId;
                config.ManagedApprovedOutputRoots = root;
                config.ManagedSetupReady = true;
                config.ManagedSetupLastResult = "Ready";
                config.ManagedPublishingApiVersion = ApiVersion;
                try
                {
                    saveConfiguration?.Invoke();
                }
                catch (Exception)
                {
                    config.ManagedPublishingIntegrationId = oldIntegrationId;
                    config.ManagedApprovedOutputRoots = oldApprovedRoots;
                    config.ManagedSetupReady = oldReady;
                    config.ManagedSetupLastResult = oldLastResult;
                    config.ManagedPublishingApiVersion = oldApiVersion;
                    return Failed("Managed setup could not be persisted.");
                }

                return Ready(integrationId, root);
            }
        }

        private static ManagedSetupResult Ready(int integrationId, string root)
        {
            return new ManagedSetupResult
            {
                CapabilityVersion = ApiVersion,
                IntegrationId = integrationId,
                ConfirmedRoot = root,
                Ready = true,
                Result = "Ready"
            };
        }

        private static ManagedSetupResult Failed(string result)
        {
            return new ManagedSetupResult
            {
                CapabilityVersion = ApiVersion,
                Ready = false,
                Result = result
            };
        }

        private static StringComparison PathComparison
        {
            get
            {
                return Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
            }
        }
    }
}
