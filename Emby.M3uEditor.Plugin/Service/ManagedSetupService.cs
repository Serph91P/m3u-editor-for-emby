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
            if (!TryGetCandidateRoot(_ownerPath, out candidate, config))
            {
                return Failed("Managed setup is not ready.");
            }

            string root;
            string validationError;
            if (!ManagedOutputPolicy.TryValidateSetupRoot(
                _ownerPath,
                candidate,
                config.ManagedApprovedOutputRoots,
                out root,
                out validationError,
                !string.IsNullOrWhiteSpace(config.ManagedPublishingOutputRoot)))
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
            if (!TryGetCandidateRoot(_ownerPath, out candidate, config))
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
                    out root,
                    out validationError,
                    !string.IsNullOrWhiteSpace(config.ManagedPublishingOutputRoot)))
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
                    config.ManagedPublishingApiVersion == ApiVersion &&
                    string.IsNullOrEmpty(config.ManagedLastError))
                {
                    return Ready(integrationId, root);
                }

                var oldIntegrationId = config.ManagedPublishingIntegrationId;
                var oldApprovedRoots = config.ManagedApprovedOutputRoots;
                var oldReady = config.ManagedSetupReady;
                var oldLastResult = config.ManagedSetupLastResult;
                var oldApiVersion = config.ManagedPublishingApiVersion;
                var oldLastError = config.ManagedLastError;
                config.ManagedPublishingIntegrationId = integrationId;
                config.ManagedApprovedOutputRoots = approvedRoots;
                config.ManagedSetupReady = true;
                config.ManagedSetupLastResult = "Ready";
                config.ManagedPublishingApiVersion = ApiVersion;
                config.ManagedLastError = string.Empty;
                var persisted = false;
                try
                {
                    saveConfiguration?.Invoke();
                    persisted = true;
                }
                catch (Exception ex) when (IsPersistenceException(ex))
                {
                    return Failed("Managed setup could not be persisted.");
                }
                finally
                {
                    if (!persisted)
                    {
                        config.ManagedPublishingIntegrationId = oldIntegrationId;
                        config.ManagedApprovedOutputRoots = oldApprovedRoots;
                        config.ManagedSetupReady = oldReady;
                        config.ManagedSetupLastResult = oldLastResult;
                        config.ManagedPublishingApiVersion = oldApiVersion;
                        config.ManagedLastError = oldLastError;
                    }
                }

                return Ready(integrationId, root);
            }
        }

        internal static bool TryGetCandidateRoot(string ownerPath, out string root, PluginConfiguration config = null)
        {
            root = null;
            string candidate;
            var explicitRoot = !string.IsNullOrWhiteSpace(config?.ManagedPublishingOutputRoot);
            if (explicitRoot)
            {
                candidate = config.ManagedPublishingOutputRoot;
            }
            else if (!ManagedOutputPolicy.TryJoinUnderRoot(ownerPath, ManagedRootName, out candidate))
            {
                return false;
            }

            string error;
            return ManagedOutputPolicy.TryValidateSetupRoot(
                ownerPath,
                candidate,
                string.Empty,
                out root,
                out error,
                explicitRoot);
        }

        internal static void ValidateConfigurationChange(
            PluginConfiguration current, PluginConfiguration requested, string ownerPath)
        {
            if (requested == null)
                throw new ArgumentNullException(nameof(requested));
            current = current ?? new PluginConfiguration();
            if (string.Equals(current.ManagedPublishingOutputRoot ?? string.Empty,
                requested.ManagedPublishingOutputRoot ?? string.Empty, StringComparison.Ordinal))
                return;

            string oldRoot;
            string newRoot;
            if (!TryGetCandidateRoot(ownerPath, out oldRoot, current) ||
                !TryGetCandidateRoot(ownerPath, out newRoot, requested))
                throw new InvalidOperationException("The managed output directory must be a safe absolute directory, not a filesystem root or plugin configuration ancestor.");

            var selected = string.IsNullOrWhiteSpace(requested.ManagedPublishingOutputRoot) ? string.Empty : newRoot;
            if (string.Equals(oldRoot, newRoot, PathComparison))
            {
                requested.ManagedPublishingOutputRoot = selected;
                return;
            }

            const string stateError = "The output directory cannot change while existing managed mappings, ownership or publishing state remain. No files were moved.";
            if (HasPublishingState(current) || HasPublishingState(requested))
                throw new InvalidOperationException(stateError);

            var oldRoots = ManagedOutputPolicy.GetCanonicalRoots(current.ManagedApprovedOutputRoots);
            if (!string.IsNullOrWhiteSpace(current.ManagedApprovedOutputRoots) && oldRoots.Count == 0)
                throw new InvalidOperationException(stateError);
            oldRoots.Add(oldRoot);
            try
            {
                if (oldRoots.Any(root => File.Exists(root) ||
                    (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())))
                    throw new InvalidOperationException(stateError);
            }
            catch (IOException)
            {
                throw new InvalidOperationException(stateError);
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException(stateError);
            }

            string normalized;
            string error;
            if (!ManagedOutputPolicy.TryValidateSetupRoot(ownerPath, newRoot,
                current.ManagedApprovedOutputRoots, out normalized, out error, selected.Length > 0))
                throw new InvalidOperationException(error);
            try
            {
                Directory.CreateDirectory(normalized);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is NotSupportedException)
            {
                throw new InvalidOperationException("The managed output directory is not locally writable.");
            }
            if (!ManagedOutputPolicy.IsLocallyWritableRoot(normalized))
                throw new InvalidOperationException("The managed output directory must support creating, writing, renaming and deleting files.");

            requested.ManagedPublishingOutputRoot = selected;
            requested.ManagedApprovedOutputRoots = string.Empty;
            requested.ManagedSetupReady = false;
            requested.ManagedSetupLastResult = "Output directory changed. Run managed setup in m3u-editor to confirm it.";
        }

        private static bool HasPublishingState(PluginConfiguration config)
        {
            return !string.IsNullOrEmpty(config.ManagedCatalogRevision) ||
                !string.IsNullOrEmpty(config.ManagedActiveGeneration) ||
                !string.IsNullOrEmpty(config.ManagedPreviousGeneration) ||
                config.ManagedLastSuccessTicks > 0 ||
                HasJsonState(config.ManagedMappingsJson) || HasJsonState(config.ManagedDirectoryOwnershipJson);
        }

        private static bool HasJsonState(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            try
            {
                using (var document = JsonDocument.Parse(value))
                    return document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 0;
            }
            catch (JsonException)
            {
                return true;
            }
        }

        private static bool IsPersistenceException(Exception exception)
        {
            if (exception is ArgumentException || exception is NotSupportedException)
            {
                return true;
            }

            if (exception is IOException || exception is UnauthorizedAccessException)
            {
                return true;
            }

            return exception is InvalidOperationException || exception is JsonException;
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
