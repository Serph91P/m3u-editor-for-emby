using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Emby.M3uEditor.Plugin.Api;

namespace Emby.M3uEditor.Plugin.Service
{
    internal sealed class ManagedSetupService
    {
        internal const int ApiVersion = 1;
        private const string ManagedRootName = "managed-publishing";
        private static readonly ConcurrentDictionary<string, object> SetupLocks =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
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

            string candidate;
            if (!TryGetCandidateRoot(_ownerPath, out candidate))
            {
                return Failed("Managed setup is not ready.");
            }

            string root;
            string validationError;
            if (!ManagedOutputPolicy.TryValidateSetupRoot(
                _ownerPath,
                candidate,
                config.ManagedApprovedOutputRoots,
                null,
                false,
                out root,
                out validationError))
            {
                return Failed("Managed setup is not ready.");
            }

            var roots = ManagedOutputPolicy.GetCanonicalRoots(config.ManagedApprovedOutputRoots);
            if (!roots.Any(value => string.Equals(value, root, PathComparison)))
            {
                return Failed("Managed setup is not ready.");
            }

            return Ready(config.ManagedPublishingIntegrationId, root);
        }

        internal ManagedSetupResult Put(PluginConfiguration config, int integrationId, Action saveConfiguration)
        {
            if (config == null || integrationId < 1)
            {
                return Failed("A positive integration binding is required.");
            }

            string candidate;
            if (!TryGetCandidateRoot(_ownerPath, out candidate))
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

                string approvedRoots;
                if (!TryBuildApprovedRoots(
                    config.ManagedApprovedOutputRoots,
                    config.ManagedMappingsJson,
                    root,
                    out approvedRoots))
                {
                    return Failed("Managed setup is not ready.");
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
                    string.Equals(config.ManagedApprovedOutputRoots, approvedRoots, PathComparison) &&
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
                config.ManagedApprovedOutputRoots = approvedRoots;
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

        internal static bool TryGetCandidateRoot(string ownerPath, out string root)
        {
            root = null;
            string candidate;
            try
            {
                candidate = Path.Combine(ownerPath ?? string.Empty, ManagedRootName);
            }
            catch (ArgumentException)
            {
                return false;
            }

            string error;
            return ManagedOutputPolicy.TryValidateSetupRoot(
                ownerPath,
                candidate,
                string.Empty,
                null,
                false,
                out root,
                out error);
        }

        private static bool TryBuildApprovedRoots(
            string existingApprovedRoots,
            string mappingsJson,
            string candidate,
            out string approvedRoots)
        {
            approvedRoots = null;
            var existing = ManagedOutputPolicy.GetCanonicalRoots(existingApprovedRoots);
            List<ManagedMappingState> mappings;
            try
            {
                mappings = string.IsNullOrWhiteSpace(mappingsJson)
                    ? new List<ManagedMappingState>()
                    : JsonSerializer.Deserialize<List<ManagedMappingState>>(mappingsJson, JsonOptions);
            }
            catch (JsonException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }

            if (mappings == null)
            {
                return false;
            }

            if (existing.Count == 0)
            {
                if (mappings.Any(mapping =>
                {
                    string error;
                    return mapping == null || !ManagedOutputPolicy.IsApproved(
                        mapping.OutputPath,
                        candidate,
                        out error);
                }))
                {
                    return false;
                }

                approvedRoots = candidate;
                return true;
            }

            if (mappings.Any(mapping =>
            {
                string error;
                return mapping == null || !ManagedOutputPolicy.IsApproved(
                    mapping.OutputPath,
                    existingApprovedRoots,
                    out error);
            }))
            {
                return false;
            }

            var retained = existing
                .Where(root => !string.Equals(root, candidate, PathComparison))
                .Where(root => mappings.Any(mapping =>
                {
                    string error;
                    return ManagedOutputPolicy.IsApproved(mapping.OutputPath, root, out error);
                }))
                .ToList();
            retained.Add(candidate);
            approvedRoots = string.Join(Environment.NewLine, retained);
            return true;
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
