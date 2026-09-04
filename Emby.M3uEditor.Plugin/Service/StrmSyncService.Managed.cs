using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Emby.M3uEditor.Plugin.Api;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Client.Models;

namespace Emby.M3uEditor.Plugin.Service
{
    internal sealed class ManagedPublishResult
    {
        public bool Success { get; set; }
        public bool Duplicate { get; set; }
        public string Revision { get; set; }
        public string PreviousRevision { get; set; }
        public int Added { get; set; }
        public int Changed { get; set; }
        public int Removed { get; set; }
        public int FileCount { get; set; }
        public int StrmFileCount { get; set; }
        public int OmittedVersions { get; set; }
        public string Error { get; set; }
    }

    internal sealed class ManagedReconcileResult
    {
        public bool Compatible { get; set; }
        public bool Success { get; set; }
        public int TotalMappings { get; set; }
        public int AppliedMappings { get; set; }
        public int DuplicateMappings { get; set; }
        public int FailedMappings { get; set; }
        public int AddedFiles { get; set; }
        public int ChangedFiles { get; set; }
        public int RemovedFiles { get; set; }
        public int OmittedVersions { get; set; }
        public string CatalogRevision { get; set; }
        public string Error { get; set; }
    }

    public sealed class ManagedMappingState
    {
        public string MappingUuid { get; set; }
        public int IntegrationId { get; set; }
        public string LibraryName { get; set; }
        public string CollectionType { get; set; }
        public string OutputPath { get; set; }
        public string ActiveRevision { get; set; }
        public string PreviousRevision { get; set; }
        public bool Success { get; set; }
        public bool Duplicate { get; set; }
        public int FileCount { get; set; }
        public int StrmFileCount { get; set; }
        public int SeriesCount { get; set; }
        public int SeasonCount { get; set; }
        public int Added { get; set; }
        public int Changed { get; set; }
        public int Removed { get; set; }
        public int OmittedVersions { get; set; }
        public string Error { get; set; }
    }

    internal sealed class ManagedGenerationManifest
    {
        public int FormatVersion { get; set; } = 1;
        public string MappingUuid { get; set; }
        public string Revision { get; set; }
        public DateTime PublishedUtc { get; set; }
        public List<ManagedManifestFile> Files { get; set; } = new List<ManagedManifestFile>();
    }

    internal sealed class ManagedManifestFile
    {
        public string RelativePath { get; set; }
        public string Sha256 { get; set; }
        public long Length { get; set; }
    }

    internal sealed class ManagedPlannedFile
    {
        public string RelativePath { get; set; }
        public string Content { get; set; }
        public string Sha256 { get; set; }
        public bool IsNfo { get; set; }
    }

    public partial class StrmSyncService
    {
        private const string ManagedMetadataDirectoryName = ".m3u-editor-for-emby";
        private const int MaximumVisibleVersions = 8;
        internal const int MaximumGeneratedFileBytes = 1024 * 1024;
        internal const long MaximumGeneratedBytes = 8L * 1024L * 1024L;
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> ManagedRootLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        internal Action<string> ManagedPhaseHook { get; set; }
        internal Action<string, string> ManagedFileMoveHook { get; set; }
        internal Func<PluginConfiguration> ManagedConfigurationProvider { get; set; }
        internal Func<string> ManagedOwnerPathProvider { get; set; }
        internal async Task<ManagedReconcileResult> ReconcileManagedAsync(
            PluginConfiguration config,
            Action saveConfig,
            IProgress<double> progress,
            CancellationToken cancellationToken,
            Action refresh)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            var result = new ManagedReconcileResult { Success = true };
            var refreshRequired = false;
            var client = new M3uEditorClient(_managedHttpClient);
            try
            {
                progress?.Report(0);
                var capability = await client.DiscoverCapabilityAsync(
                    config.BaseUrl,
                    config.Username,
                    config.Password,
                    cancellationToken).ConfigureAwait(false);
                if (capability == null)
                {
                    config.ManagedPublishingEnabled = false;
                    config.ManagedPublishingApiVersion = 0;
                    config.ManagedLastError = string.Empty;
                    saveConfig?.Invoke();
                    progress?.Report(100);
                    return result;
                }

                result.Compatible = true;
                config.ManagedPublishingEnabled = false;
                config.ManagedPublishingApiVersion = capability.ApiVersion;
                string canonicalRoot;
                string setupError;
                if (!TryGetCanonicalSetupRoot(config, true, out canonicalRoot, out setupError))
                {
                    config.ManagedPublishingEnabled = false;
                    progress?.Report(100);
                    return RecordManagedReconcileFailure(
                        config,
                        saveConfig,
                        result,
                        setupError);
                }

                var approvedOutputRoots = config.ManagedApprovedOutputRoots;
                var writablePaths = new List<string> { canonicalRoot };

                await client.RegisterPublisherAsync(
                    config.BaseUrl,
                    config.Username,
                    config.Password,
                    capability.RegisterPublisherAction,
                    config.ManagedPublishingIntegrationId,
                    writablePaths,
                    cancellationToken).ConfigureAwait(false);
                progress?.Report(10);
                var catalog = await client.GetCatalogAsync(
                    config.BaseUrl,
                    config.Username,
                    config.Password,
                    cancellationToken).ConfigureAwait(false);
                if (catalog.Mappings.Any(mapping =>
                    mapping.IntegrationId != config.ManagedPublishingIntegrationId))
                {
                    return RecordManagedReconcileFailure(
                        config,
                        saveConfig,
                        result,
                        "Managed catalog mapping integration ID does not match the configured publisher.");
                }

                result.CatalogRevision = catalog.Revision;
                result.TotalMappings = catalog.Mappings.Count;
                var previousCatalogRevision = config.ManagedCatalogRevision;
                config.ManagedCatalogRevision = catalog.Revision;

                var states = new List<ManagedMappingState>();
                for (var index = 0; index < catalog.Mappings.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mapping = catalog.Mappings[index];
                    ManagedPublishResult published;
                    string approvalError;
                    if (!ManagedOutputPolicy.IsApproved(
                        mapping.TargetLibrary.OutputPath,
                        approvedOutputRoots,
                        out approvalError))
                    {
                        published = Failed(mapping.Revision, approvalError);
                    }
                    else
                    {
                        published = await PublishManagedMappingAsync(
                            mapping,
                            cancellationToken,
                            approvedOutputRoots,
                            config).ConfigureAwait(false);
                    }
                    var activeGenerationChanged = published.Success && !published.Duplicate;

                    string currentRoot;
                    string currentApprovedRoots;
                    if (!TryGetCurrentSetupRoot(
                        config,
                        mapping.IntegrationId,
                        canonicalRoot,
                        approvedOutputRoots,
                        out currentRoot,
                        out currentApprovedRoots,
                        out setupError) ||
                        !ManagedOutputPolicy.IsApproved(
                            mapping.TargetLibrary.OutputPath,
                            currentApprovedRoots,
                            out setupError))
                    {
                        if (published.Success)
                        {
                            await RevertAfterCallbackFailure(
                                mapping,
                                published,
                                cancellationToken).ConfigureAwait(false);
                        }

                        config.ManagedCatalogRevision = previousCatalogRevision;
                        return RecordManagedReconcileFailure(
                            config,
                            saveConfig,
                            result,
                            setupError);
                    }

                    if (published.Success)
                    {
                        try
                        {
                            var callback = await client.ReportSyncResultAsync(
                                config.BaseUrl,
                                config.Username,
                                config.Password,
                                mapping.IntegrationId,
                                mapping.MappingUuid,
                                mapping.Revision,
                                true,
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "Published {0} managed files; omitted {1} visible versions.",
                                    published.FileCount,
                                    published.OmittedVersions),
                                null,
                                cancellationToken).ConfigureAwait(false);
                            published.Duplicate = published.Duplicate || callback.Duplicate;
                        }
                        catch (HttpRequestException)
                        {
                            published = await RevertAfterCallbackFailure(mapping, published, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            await RevertAfterCallbackFailure(mapping, published, CancellationToken.None)
                                .ConfigureAwait(false);
                            throw;
                        }
                        catch (TaskCanceledException)
                        {
                            published = await RevertAfterCallbackFailure(mapping, published, cancellationToken).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException)
                        {
                            published = await RevertAfterCallbackFailure(mapping, published, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await ReportManagedFailure(client, config, mapping, published.Error, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (published.Success)
                    {
                        result.AppliedMappings++;
                        if (published.Duplicate)
                        {
                            result.DuplicateMappings++;
                        }

                        config.ManagedActiveGeneration = mapping.Revision;
                        config.ManagedPreviousGeneration = published.PreviousRevision ?? string.Empty;
                        refreshRequired = refreshRequired || (activeGenerationChanged && mapping.Options.Refresh);
                    }
                    else
                    {
                        result.Success = false;
                        result.FailedMappings++;
                        result.Error = published.Error;
                    }

                    result.OmittedVersions += published.OmittedVersions;
                    result.AddedFiles += published.Added;
                    result.ChangedFiles += published.Changed;
                    result.RemovedFiles += published.Removed;
                    states.Add(new ManagedMappingState
                    {
                        MappingUuid = mapping.MappingUuid,
                        IntegrationId = mapping.IntegrationId,
                        LibraryName = mapping.TargetLibrary.Name,
                        CollectionType = mapping.TargetLibrary.CollectionType,
                        OutputPath = mapping.TargetLibrary.OutputPath,
                        ActiveRevision = published.Success ? mapping.Revision : published.PreviousRevision,
                        PreviousRevision = published.PreviousRevision,
                        Success = published.Success,
                        Duplicate = published.Duplicate,
                        FileCount = published.FileCount,
                        StrmFileCount = published.StrmFileCount,
                        SeriesCount = mapping.Items.Count(item =>
                            string.Equals(item.MediaType, "series", StringComparison.Ordinal)),
                        SeasonCount = mapping.Items
                            .Where(item => string.Equals(item.MediaType, "series", StringComparison.Ordinal))
                            .Sum(item => (item.Episodes ?? new List<M3uEditorCatalogItem>())
                                .Select(episode => episode.SeasonNumber)
                                .Distinct()
                                .Count()),
                        Added = published.Added,
                        Changed = published.Changed,
                        Removed = published.Removed,
                        OmittedVersions = published.OmittedVersions,
                        Error = published.Error
                    });
                    progress?.Report(10 + (catalog.Mappings.Count == 0
                        ? 85
                        : 85.0 * (index + 1) / catalog.Mappings.Count));
                }

                if (result.Success && refreshRequired)
                {
                    if (refresh == null)
                    {
                        return RecordManagedReconcileFailure(
                            config,
                            saveConfig,
                            result,
                            "Managed reconcile failed.");
                    }

                    try
                    {
                        refresh();
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        return RecordManagedReconcileFailure(
                            config,
                            saveConfig,
                            result,
                            "Managed reconcile failed.");
                    }
                }

                config.ManagedMappingsJson = JsonSerializer.Serialize(states, JsonOptions);
                config.ManagedOmittedVersions = result.OmittedVersions;
                config.ManagedDryRunSummary = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} mapping(s), {1} applied, {2} failed; {3} added, {4} changed, {5} removed; {6} omitted versions.",
                    result.TotalMappings,
                    result.AppliedMappings,
                    result.FailedMappings,
                    result.AddedFiles,
                    result.ChangedFiles,
                    result.RemovedFiles,
                    result.OmittedVersions);
                config.ManagedLastError = result.Success ? string.Empty : result.Error ?? "Managed reconcile failed.";
                config.ManagedPublishingEnabled = result.Success;
                if (result.Success)
                {
                    config.ManagedLastSuccessTicks = DateTime.UtcNow.Ticks;
                }

                saveConfig?.Invoke();
                progress?.Report(100);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                return RecordManagedReconcileFailure(config, saveConfig, result, "Managed backend request failed.");
            }
            catch (TaskCanceledException)
            {
                return RecordManagedReconcileFailure(config, saveConfig, result, "Managed backend request timed out.");
            }
            catch (ObjectDisposedException)
            {
                return RecordManagedReconcileFailure(config, saveConfig, result, "Managed reconcile failed.");
            }
            catch (InvalidOperationException ex)
            {
                return RecordManagedReconcileFailure(config, saveConfig, result, ex.Message);
            }
            catch (Exception)
            {
                return RecordManagedReconcileFailure(config, saveConfig, result, "Managed reconcile failed.");
            }
        }

        private async Task<ManagedPublishResult> RevertAfterCallbackFailure(
            M3uEditorMapping mapping,
            ManagedPublishResult published,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(published.PreviousRevision))
            {
                var rollback = await RollbackManagedMappingAsync(
                    mapping.TargetLibrary.OutputPath,
                    mapping.MappingUuid,
                    cancellationToken,
                    null,
                    null).ConfigureAwait(false);
                if (!rollback.Success)
                {
                    return Failed(mapping.Revision, rollback.Error, published);
                }
            }
            else if (!published.Duplicate)
            {
                RemoveOnlyManagedGeneration(
                    mapping.TargetLibrary.OutputPath,
                    mapping.MappingUuid);
            }

            return Failed(mapping.Revision, "Managed sync callback failed; the prior generation was restored.", published);
        }

        private string ResolveCurrentApprovedOutputRoots()
        {
            var current = ManagedConfigurationProvider == null
                ? Plugin.InstanceOrNull?.Configuration
                : ManagedConfigurationProvider();
            return current == null ? null : current.ManagedApprovedOutputRoots;
        }

        private bool TryGetCanonicalSetupRoot(
            PluginConfiguration config,
            bool allowLegacyMigration,
            out string root,
            out string error)
        {
            root = null;
            error = "Managed publishing setup is not ready.";
            if (config == null || config.ManagedPublishingIntegrationId < 1)
            {
                error = "Managed publishing integration ID must be a positive integer.";
                return false;
            }

            if (!config.ManagedSetupReady && !allowLegacyMigration)
            {
                return false;
            }

            if (!config.ManagedSetupReady && !string.IsNullOrEmpty(config.ManagedSetupLastResult))
            {
                return false;
            }

            var roots = ManagedOutputPolicy.GetCanonicalRoots(config.ManagedApprovedOutputRoots);
            if (roots.Count == 0)
            {
                error = "Managed publishing requires one canonical approved root.";
                return false;
            }

            if (roots.Count == 1)
            {
                root = roots[0];
            }
            else
            {
                string candidate;
                var ownerPath = ManagedOwnerPathProvider == null
                    ? Plugin.InstanceOrNull?.DataFolderPath
                    : ManagedOwnerPathProvider();
                if (!ManagedSetupService.TryGetCandidateRoot(ownerPath, out candidate) ||
                    !roots.Any(value => string.Equals(value, candidate, PathComparison)))
                {
                    error = "Managed publishing requires one companion-owned canonical root.";
                    return false;
                }

                root = candidate;
            }

            if (!ManagedOutputPolicy.IsLocallyWritableRoot(root))
            {
                root = null;
                error = "The canonical managed output root is not locally writable.";
                return false;
            }

            if (!config.ManagedSetupReady)
            {
                config.ManagedSetupReady = true;
                config.ManagedSetupLastResult = "Migrated legacy managed setup";
            }

            error = string.Empty;
            return true;
        }

        private bool TryGetCurrentSetupRoot(
            PluginConfiguration fallback,
            int expectedIntegrationId,
            string expectedRoot,
            string expectedApprovedRoots,
            out string currentRoot,
            out string currentApprovedRoots,
            out string error)
        {
            var current = ManagedConfigurationProvider == null
                ? fallback
                : ManagedConfigurationProvider();
            currentApprovedRoots = current == null ? null : current.ManagedApprovedOutputRoots;
            if (!TryGetCanonicalSetupRoot(current, false, out currentRoot, out error) ||
                current.ManagedPublishingIntegrationId != expectedIntegrationId ||
                !string.Equals(currentRoot, expectedRoot, PathComparison) ||
                !string.Equals(currentApprovedRoots, expectedApprovedRoots, PathComparison))
            {
                currentRoot = null;
                currentApprovedRoots = null;
                error = "Managed publishing setup is no longer current.";
                return false;
            }

            return true;
        }

        private static async Task ReportManagedFailure(
            M3uEditorClient client,
            PluginConfiguration config,
            M3uEditorMapping mapping,
            string error,
            CancellationToken cancellationToken)
        {
            try
            {
                await client.ReportSyncResultAsync(
                    config.BaseUrl,
                    config.Username,
                    config.Password,
                    mapping.IntegrationId,
                    mapping.MappingUuid,
                    mapping.Revision,
                    false,
                    "Managed publication failed.",
                    error,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static ManagedReconcileResult RecordManagedReconcileFailure(
            PluginConfiguration config,
            Action saveConfig,
            ManagedReconcileResult result,
            string error)
        {
            result.Success = false;
            result.Error = error;
            config.ManagedPublishingEnabled = false;
            config.ManagedLastError = error;
            saveConfig?.Invoke();
            return result;
        }

        private void RemoveOnlyManagedGeneration(
            string outputRoot,
            string mappingUuid)
        {
            var root = Path.GetFullPath(outputRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var metadataRoot = Path.Combine(root, ManagedMetadataDirectoryName);
            var manifest = ReadManifest(Path.Combine(metadataRoot, "active.json"));
            if (manifest == null ||
                !string.Equals(manifest.MappingUuid, mappingUuid, StringComparison.OrdinalIgnoreCase) ||
                !ManifestFilesAreValid(root, manifest))
            {
                throw new InvalidOperationException("The failed managed generation could not be safely removed.");
            }

            string approvalError;
            if (!ManagedOutputPolicy.IsApproved(
                root,
                ResolveCurrentApprovedOutputRoots(),
                out approvalError))
            {
                throw new InvalidOperationException(approvalError);
            }

            foreach (var file in manifest.Files)
            {
                File.Delete(CombineUnderRoot(root, file.RelativePath));
            }

            Directory.Delete(metadataRoot, true);
        }

        internal async Task<ManagedPublishResult> PublishManagedMappingAsync(
            M3uEditorMapping mapping,
            CancellationToken cancellationToken,
            string approvedOutputRoots = null,
            PluginConfiguration setupConfig = null)
        {
            string approvalError;
            if (approvedOutputRoots != null && !ManagedOutputPolicy.IsApproved(
                mapping == null || mapping.TargetLibrary == null ? null : mapping.TargetLibrary.OutputPath,
                approvedOutputRoots,
                out approvalError))
            {
                return Failed(mapping == null ? null : mapping.Revision, approvalError);
            }

            M3uEditorCatalogValidator.Validate(new M3uEditorCatalog
            {
                ApiVersion = 1,
                FullSnapshot = true,
                Revision = "0000000000000000000000000000000000000000000000000000000000000000",
                Mappings = new List<M3uEditorMapping> { mapping }
            });

            var root = Path.GetFullPath(mapping.TargetLibrary.OutputPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootLock = ManagedRootLocks.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
            if (!await rootLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return Failed(mapping.Revision, "A managed publication is already running for this output root.");
            }

            try
            {
                return PublishManagedMapping(
                    mapping,
                    root,
                    cancellationToken,
                    approvedOutputRoots,
                    setupConfig);
            }
            finally
            {
                rootLock.Release();
            }
        }

        internal async Task<ManagedPublishResult> RollbackManagedMappingAsync(
            string outputRoot,
            string mappingUuid,
            CancellationToken cancellationToken,
            Action refresh,
            string approvedOutputRoots)
        {
            string approvalError;
            if (!ManagedOutputPolicy.IsApproved(
                outputRoot,
                ResolveCurrentApprovedOutputRoots(),
                out approvalError))
            {
                return Failed(null, approvalError);
            }

            Guid parsedMappingUuid;
            if (string.IsNullOrWhiteSpace(outputRoot) || !Guid.TryParse(mappingUuid, out parsedMappingUuid))
            {
                return Failed(null, "Managed rollback identity is invalid.");
            }

            var root = Path.GetFullPath(outputRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootLock = ManagedRootLocks.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
            if (!await rootLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return Failed(null, "A managed publication is already running for this output root.");
            }

            try
            {
                if (!ManagedOutputPolicy.IsApproved(
                    root,
                    ResolveCurrentApprovedOutputRoots(),
                    out approvalError))
                {
                    return Failed(null, approvalError);
                }

                var rolledBack = RollbackManagedMapping(root, mappingUuid, cancellationToken);
                if (!rolledBack.Success || refresh == null)
                {
                    return rolledBack;
                }

                try
                {
                    refresh();
                    return rolledBack;
                }
                catch (InvalidOperationException)
                {
                    if (!ManagedOutputPolicy.IsApproved(
                        root,
                        ResolveCurrentApprovedOutputRoots(),
                        out approvalError))
                    {
                        return Failed(rolledBack.Revision, approvalError, rolledBack);
                    }

                    var restored = RollbackManagedMapping(root, mappingUuid, CancellationToken.None);
                    return Failed(
                        restored.Revision,
                        "Managed rollback refresh failed; the prior active generation was restored.",
                        restored);
                }
                catch (ArgumentException)
                {
                    if (!ManagedOutputPolicy.IsApproved(
                        root,
                        ResolveCurrentApprovedOutputRoots(),
                        out approvalError))
                    {
                        return Failed(rolledBack.Revision, approvalError, rolledBack);
                    }

                    var restored = RollbackManagedMapping(root, mappingUuid, CancellationToken.None);
                    return Failed(
                        restored.Revision,
                        "Managed rollback refresh failed; the prior active generation was restored.",
                        restored);
                }
            }
            finally
            {
                rootLock.Release();
            }
        }

        private ManagedPublishResult RollbackManagedMapping(
            string root,
            string mappingUuid,
            CancellationToken cancellationToken)
        {
            var metadataRoot = Path.Combine(root, ManagedMetadataDirectoryName);
            var activeManifestPath = Path.Combine(metadataRoot, "active.json");
            var previousManifestPath = Path.Combine(metadataRoot, "previous.json");
            var previousFilesRoot = Path.Combine(metadataRoot, "previous-files");
            var currentFilesRoot = Path.Combine(metadataRoot, "rollback-current");
            ManagedGenerationManifest active = null;
            ManagedGenerationManifest previous = null;
            var currentMoved = new List<string>();
            var previousMoved = new List<string>();

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureWritableManagedRoot(root);
                active = ReadManifest(activeManifestPath);
                previous = ReadManifest(previousManifestPath);
                if (active == null || previous == null ||
                    !string.Equals(active.MappingUuid, mappingUuid, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(previous.MappingUuid, mappingUuid, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("No previous valid managed generation is available.");
                }

                if (!ManifestFilesAreValid(root, active) ||
                    !Directory.Exists(previousFilesRoot) ||
                    !ManifestFilesAreValid(previousFilesRoot, previous))
                {
                    throw new InvalidOperationException("The active or previous managed generation is invalid.");
                }

                DeleteOwnedDirectory(currentFilesRoot);
                MoveManifestFiles(root, currentFilesRoot, active, "rollback-active", currentMoved);
                MoveManifestFiles(previousFilesRoot, root, previous, "rollback-previous", previousMoved);

                WriteManifestAtomic(activeManifestPath, previous);
                WriteManifestAtomic(previousManifestPath, active);
                Directory.Delete(previousFilesRoot, true);
                Directory.Move(currentFilesRoot, previousFilesRoot);

                return new ManagedPublishResult
                {
                    Success = true,
                    Revision = previous.Revision,
                    PreviousRevision = active.Revision,
                    FileCount = previous.Files.Count,
                    StrmFileCount = previous.Files.Count(file =>
                        file.RelativePath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                };
            }
            catch (OperationCanceledException)
            {
                RestoreFailedRollback(root, previousFilesRoot, currentFilesRoot, active, previous, currentMoved, previousMoved);
                throw;
            }
            catch (IOException)
            {
                RestoreFailedRollback(root, previousFilesRoot, currentFilesRoot, active, previous, currentMoved, previousMoved);
                return Failed(active?.Revision, "Managed rollback failed during filesystem commit.");
            }
            catch (UnauthorizedAccessException)
            {
                RestoreFailedRollback(root, previousFilesRoot, currentFilesRoot, active, previous, currentMoved, previousMoved);
                return Failed(active?.Revision, "Managed rollback failed because the output root is not writable.");
            }
            catch (InvalidOperationException ex)
            {
                RestoreFailedRollback(root, previousFilesRoot, currentFilesRoot, active, previous, currentMoved, previousMoved);
                return Failed(active?.Revision, ex.Message);
            }
            catch (JsonException)
            {
                RestoreFailedRollback(root, previousFilesRoot, currentFilesRoot, active, previous, currentMoved, previousMoved);
                return Failed(active?.Revision, "Managed generation manifest is invalid.");
            }
        }

        private void RestoreFailedRollback(
            string root,
            string previousFilesRoot,
            string currentFilesRoot,
            ManagedGenerationManifest active,
            ManagedGenerationManifest previous,
            List<string> currentMoved,
            List<string> previousMoved)
        {
            if (previousMoved.Count > 0)
            {
                RestoreMovedFiles(root, previousFilesRoot, previousMoved, "restore-rollback-previous");
            }

            if (currentMoved.Count > 0)
            {
                RestoreMovedFiles(currentFilesRoot, root, currentMoved, "restore-rollback-active");
            }

            if (active != null)
            {
                WriteManifestAtomic(Path.Combine(root, ManagedMetadataDirectoryName, "active.json"), active);
            }

            if (previous != null)
            {
                WriteManifestAtomic(Path.Combine(root, ManagedMetadataDirectoryName, "previous.json"), previous);
            }
        }

        private ManagedPublishResult PublishManagedMapping(
            M3uEditorMapping mapping,
            string root,
            CancellationToken cancellationToken,
            string approvedOutputRoots,
            PluginConfiguration setupConfig)
        {
            var result = new ManagedPublishResult { Revision = mapping.Revision };
            string stagingRoot = null;
            string previousFilesRoot = null;
            string previousBackupRoot = null;
            ManagedGenerationManifest activeManifest = null;
            ManagedGenerationManifest previousManifest = null;
            List<ManagedPlannedFile> plan = null;
            var quarantinedFiles = new List<string>();
            var publishedFiles = new List<string>();
            var mutationStarted = false;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadataRoot = Path.Combine(root, ManagedMetadataDirectoryName);
                EnsureNoReparsePoint(root, metadataRoot);
                var activeManifestPath = Path.Combine(metadataRoot, "active.json");
                if (Directory.Exists(metadataRoot) && !File.Exists(activeManifestPath) &&
                    Directory.EnumerateFileSystemEntries(metadataRoot).Any())
                {
                    throw new InvalidOperationException("Managed metadata path contains files not owned by this plugin.");
                }
                var previousManifestPath = Path.Combine(metadataRoot, "previous.json");
                activeManifest = ReadManifest(activeManifestPath);
                previousManifest = ReadManifest(previousManifestPath);
                if (activeManifest != null &&
                    !string.Equals(activeManifest.MappingUuid, mapping.MappingUuid, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The output root is owned by a different managed mapping.");
                }

                plan = BuildManagedPlan(mapping, result);
                ValidateGeneratedOutputBudget(plan);
                if (activeManifest != null &&
                    !string.Equals(mapping.Options.Cleanup, "replace", StringComparison.Ordinal))
                {
                    RetainStaleManagedFiles(root, plan, activeManifest);
                }

                ValidateGeneratedOutputBudget(plan);
                ValidatePlan(root, plan, activeManifest);

                if (activeManifest != null &&
                    string.Equals(activeManifest.Revision, mapping.Revision, StringComparison.Ordinal) &&
                    ManifestFilesAreValid(root, activeManifest))
                {
                    result.Success = true;
                    result.Duplicate = true;
                    result.FileCount = activeManifest.Files.Count;
                    result.StrmFileCount = activeManifest.Files.Count(file =>
                        file.RelativePath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase));
                    result.PreviousRevision = ReadManifest(previousManifestPath)?.Revision;
                    return result;
                }

                ComputeDiff(plan, activeManifest, result);
                EnsureWritableManagedRoot(root);
                Directory.CreateDirectory(metadataRoot);
                stagingRoot = Path.Combine(metadataRoot, "staging-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stagingRoot);
                WriteAndValidateStaging(stagingRoot, plan, cancellationToken);
                InvokeManagedPhase("after-stage");
                EnsureCurrentSetupApproval(mapping, root, approvedOutputRoots, setupConfig);

                previousFilesRoot = Path.Combine(metadataRoot, "previous-files");
                if (Directory.Exists(previousFilesRoot))
                {
                    if (previousManifest == null || !ManifestOwnsDirectory(previousFilesRoot, previousManifest))
                    {
                        throw new InvalidOperationException("The previous managed generation is invalid.");
                    }

                    previousBackupRoot = Path.Combine(metadataRoot, "previous-backup-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(previousFilesRoot, previousBackupRoot);
                    mutationStarted = true;
                }
                else if (previousManifest != null)
                {
                    throw new InvalidOperationException("The previous managed generation files are missing.");
                }

                if (activeManifest != null)
                {
                    if (!ManifestFilesAreValid(root, activeManifest))
                    {
                        throw new InvalidOperationException("The active managed generation no longer matches its manifest.");
                    }

                    mutationStarted = true;
                    MoveManifestFiles(root, previousFilesRoot, activeManifest, "quarantine", quarantinedFiles);
                    WriteManifestAtomic(previousManifestPath, activeManifest);
                    result.PreviousRevision = activeManifest.Revision;
                    InvokeManagedPhase("after-quarantine");
                }

                mutationStarted = true;
                foreach (var file in plan)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = CombineUnderRoot(stagingRoot, file.RelativePath);
                    var destination = CombineUnderRoot(root, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    InvokeManagedFileMove("publish", file.RelativePath);
                    File.Move(source, destination);
                    publishedFiles.Add(file.RelativePath);
                }

                InvokeManagedPhase("after-publish");

                var newManifest = new ManagedGenerationManifest
                {
                    MappingUuid = mapping.MappingUuid,
                    Revision = mapping.Revision,
                    PublishedUtc = DateTime.UtcNow,
                    Files = plan.Select(file => new ManagedManifestFile
                    {
                        RelativePath = file.RelativePath,
                        Sha256 = file.Sha256,
                        Length = Encoding.UTF8.GetByteCount(file.Content)
                    }).ToList()
                };
                InvokeManagedPhase("before-manifest");
                EnsureCurrentSetupApproval(mapping, root, approvedOutputRoots, setupConfig);
                WriteManifestAtomic(activeManifestPath, newManifest);
                Directory.Delete(stagingRoot, true);
                stagingRoot = null;
                DeleteOwnedDirectory(previousBackupRoot);
                previousBackupRoot = null;

                result.Success = true;
                result.FileCount = plan.Count;
                result.StrmFileCount = plan.Count(file => !file.IsNfo);
                return result;
            }
            catch (OperationCanceledException)
            {
                if (mutationStarted)
                {
                    RestoreManagedGeneration(
                        root,
                        activeManifest,
                        previousManifest,
                        previousFilesRoot,
                        previousBackupRoot,
                        quarantinedFiles,
                        publishedFiles);
                }
                DeleteOwnedDirectory(stagingRoot);
                throw;
            }
            catch (IOException)
            {
                if (mutationStarted)
                {
                    RestoreManagedGeneration(
                        root,
                        activeManifest,
                        previousManifest,
                        previousFilesRoot,
                        previousBackupRoot,
                        quarantinedFiles,
                        publishedFiles);
                }
                DeleteOwnedDirectory(stagingRoot);
                return Failed(mapping.Revision, "Managed publication failed during filesystem commit.", result);
            }
            catch (UnauthorizedAccessException)
            {
                if (mutationStarted)
                {
                    RestoreManagedGeneration(
                        root,
                        activeManifest,
                        previousManifest,
                        previousFilesRoot,
                        previousBackupRoot,
                        quarantinedFiles,
                        publishedFiles);
                }
                DeleteOwnedDirectory(stagingRoot);
                return Failed(mapping.Revision, "Managed publication failed because the output root is not writable.", result);
            }
            catch (InvalidOperationException ex)
            {
                if (mutationStarted)
                {
                    RestoreManagedGeneration(
                        root,
                        activeManifest,
                        previousManifest,
                        previousFilesRoot,
                        previousBackupRoot,
                        quarantinedFiles,
                        publishedFiles);
                }
                DeleteOwnedDirectory(stagingRoot);
                return Failed(mapping.Revision, ex.Message, result);
            }
            catch (JsonException)
            {
                if (mutationStarted)
                {
                    RestoreManagedGeneration(
                        root,
                        activeManifest,
                        previousManifest,
                        previousFilesRoot,
                        previousBackupRoot,
                        quarantinedFiles,
                        publishedFiles);
                }
                DeleteOwnedDirectory(stagingRoot);
                return Failed(mapping.Revision, "Managed generation manifest is invalid.", result);
            }
        }

        private static List<ManagedPlannedFile> BuildManagedPlan(
            M3uEditorMapping mapping,
            ManagedPublishResult result)
        {
            var plan = new List<ManagedPlannedFile>();
            foreach (var item in mapping.Items.OrderBy(value => value.CanonicalId, StringComparer.Ordinal))
            {
                if (string.Equals(item.MediaType, "movie", StringComparison.Ordinal))
                {
                    AddVariantFiles(plan, item.RelativeFolder, item, mapping.Options.Versions, result);
                    if (mapping.Options.Nfo)
                    {
                        AddPlanFile(plan, CombineRelative(item.RelativeFolder, "movie.nfo"), BuildNfo("movie", item.Nfo), true);
                    }

                    continue;
                }

                if (mapping.Options.Nfo)
                {
                    AddPlanFile(plan, CombineRelative(item.RelativeFolder, "tvshow.nfo"), BuildNfo("tvshow", item.Nfo), true);
                }

                foreach (var episode in item.Episodes
                    .OrderBy(value => value.SeasonNumber)
                    .ThenBy(value => value.EpisodeNumber)
                    .ThenBy(value => value.CanonicalId, StringComparer.Ordinal))
                {
                    var episodeFolder = CombineRelative(item.RelativeFolder, episode.RelativeFolder);
                    AddVariantFiles(plan, episodeFolder, episode, mapping.Options.Versions, result);
                    if (mapping.Options.Nfo)
                    {
                        AddPlanFile(plan, CombineRelative(episodeFolder, episode.BaseFilename + ".nfo"), BuildNfo("episodedetails", episode.Nfo), true);
                    }
                }
            }

            var duplicate = plan.GroupBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
            {
                throw new InvalidOperationException("Managed publication contains duplicate filenames.");
            }

            return plan.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList();
        }

        private static void AddVariantFiles(
            List<ManagedPlannedFile> plan,
            string folder,
            M3uEditorCatalogItem item,
            bool versionsEnabled,
            ManagedPublishResult result)
        {
            var ordered = item.Variants.OrderBy(variant => variant.Key, StringComparer.Ordinal).ToList();
            var visible = versionsEnabled ? ordered.Take(MaximumVisibleVersions).ToList() : ordered.Take(1).ToList();
            result.OmittedVersions += Math.Max(0, ordered.Count - visible.Count);
            foreach (var variant in visible)
            {
                var filename = versionsEnabled
                    ? item.BaseFilename + " - " + variant.Key + ".strm"
                    : item.BaseFilename + ".strm";
                AddPlanFile(plan, CombineRelative(folder, filename), variant.Preferred.PlaybackUrl.Trim() + "\n", false);
            }
        }

        private static void AddPlanFile(
            List<ManagedPlannedFile> plan,
            string relativePath,
            string content,
            bool isNfo)
        {
            plan.Add(new ManagedPlannedFile
            {
                RelativePath = relativePath,
                Content = content,
                Sha256 = ComputeManagedHash(content),
                IsNfo = isNfo
            });
        }

        private static string BuildNfo(string rootName, M3uEditorNfo nfo)
        {
            var root = new XElement(rootName);
            AddElement(root, "title", nfo.Title);
            AddElement(root, "originaltitle", nfo.OriginalTitle);
            if (nfo.Year.HasValue)
            {
                AddElement(root, "year", nfo.Year.Value.ToString(CultureInfo.InvariantCulture));
            }

            AddElement(root, "plot", nfo.Plot);
            if (nfo.SeasonNumber.HasValue)
            {
                AddElement(root, "season", nfo.SeasonNumber.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (nfo.EpisodeNumber.HasValue)
            {
                AddElement(root, "episode", nfo.EpisodeNumber.Value.ToString(CultureInfo.InvariantCulture));
            }

            foreach (var genre in GetGenres(nfo.Genres))
            {
                AddElement(root, "genre", genre);
            }

            AddProviderId(root, "tmdb", nfo.Ids.Tmdb?.ToString(CultureInfo.InvariantCulture));
            AddProviderId(root, "tvdb", nfo.Ids.Tvdb?.ToString(CultureInfo.InvariantCulture));
            AddProviderId(root, "imdb", nfo.Ids.Imdb);
            return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString();
        }

        private static IEnumerable<string> GetGenres(JsonElement genres)
        {
            if (genres.ValueKind == JsonValueKind.String)
            {
                return (genres.GetString() ?? string.Empty)
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Trim())
                    .Where(value => value.Length > 0);
            }

            if (genres.ValueKind == JsonValueKind.Array)
            {
                return genres.EnumerateArray()
                    .Select(value => value.GetString()?.Trim())
                    .Where(value => !string.IsNullOrEmpty(value))
                    .ToList();
            }

            return Enumerable.Empty<string>();
        }

        private static void AddElement(XElement parent, string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parent.Add(new XElement(name, value));
            }
        }

        private static void AddProviderId(XElement parent, string type, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parent.Add(new XElement("uniqueid", new XAttribute("type", type), value));
            }
        }

        private static void ValidatePlan(
            string root,
            List<ManagedPlannedFile> plan,
            ManagedGenerationManifest activeManifest)
        {
            var owned = new HashSet<string>(
                activeManifest?.Files.Select(file => file.RelativePath) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (var file in plan)
            {
                var target = CombineUnderRoot(root, file.RelativePath);
                EnsureNoReparsePoint(root, target);
                if (File.Exists(target) && !owned.Contains(file.RelativePath))
                {
                    throw new InvalidOperationException("Managed publication would overwrite a foreign file.");
                }

                if (file.IsNfo)
                {
                    XDocument.Parse(file.Content, LoadOptions.None);
                }
                else
                {
                    Uri playbackUri;
                    if (!Uri.TryCreate(file.Content.Trim(), UriKind.Absolute, out playbackUri) ||
                        (playbackUri.Scheme != Uri.UriSchemeHttp && playbackUri.Scheme != Uri.UriSchemeHttps))
                    {
                        throw new InvalidOperationException("Managed publication contains an invalid playback URL.");
                    }
                }
            }
        }

        private static void RetainStaleManagedFiles(
            string root,
            List<ManagedPlannedFile> plan,
            ManagedGenerationManifest activeManifest)
        {
            if (!ManifestFilesAreValid(root, activeManifest))
            {
                throw new InvalidOperationException("The active managed generation no longer matches its manifest.");
            }

            var plannedPaths = new HashSet<string>(
                plan.Select(file => file.RelativePath),
                StringComparer.OrdinalIgnoreCase);
            var plannedBytes = GetGeneratedOutputBytes(plan);
            foreach (var staleFile in activeManifest.Files)
            {
                if (plannedPaths.Contains(staleFile.RelativePath))
                {
                    continue;
                }

                if (staleFile.Length > MaximumGeneratedFileBytes)
                {
                    throw new InvalidOperationException("Managed publication generated file byte limit exceeded.");
                }

                plannedBytes = AddGeneratedBytes(plannedBytes, staleFile.Length);
                var content = File.ReadAllText(CombineUnderRoot(root, staleFile.RelativePath), Encoding.UTF8);
                plan.Add(new ManagedPlannedFile
                {
                    RelativePath = staleFile.RelativePath,
                    Content = content,
                    Sha256 = staleFile.Sha256,
                    IsNfo = staleFile.RelativePath.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase)
                });
            }

            plan.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        }

        private static void ValidateGeneratedOutputBudget(IEnumerable<ManagedPlannedFile> plan)
        {
            GetGeneratedOutputBytes(plan);
        }

        private static long GetGeneratedOutputBytes(IEnumerable<ManagedPlannedFile> plan)
        {
            long total = 0;
            foreach (var file in plan)
            {
                var fileBytes = Encoding.UTF8.GetByteCount(file.Content);
                if (fileBytes > MaximumGeneratedFileBytes)
                {
                    throw new InvalidOperationException("Managed publication generated file byte limit exceeded.");
                }

                total = AddGeneratedBytes(total, fileBytes);
            }

            return total;
        }

        private static long AddGeneratedBytes(long total, long fileBytes)
        {
            long next;
            try
            {
                next = checked(total + fileBytes);
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException("Managed publication aggregate generated byte limit exceeded.");
            }

            if (next > MaximumGeneratedBytes)
            {
                throw new InvalidOperationException("Managed publication aggregate generated byte limit exceeded.");
            }

            return next;
        }

        private static void WriteAndValidateStaging(
            string stagingRoot,
            List<ManagedPlannedFile> plan,
            CancellationToken cancellationToken)
        {
            foreach (var file in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = CombineUnderRoot(stagingRoot, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temporaryPath, file.Content, new UTF8Encoding(false));
                File.Move(temporaryPath, path);
                var writtenContent = File.ReadAllText(path, Encoding.UTF8);
                if (!string.Equals(ComputeManagedHash(writtenContent), file.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Managed staging hash validation failed.");
                }

                if (file.IsNfo)
                {
                    XDocument.Load(path, LoadOptions.None);
                }
            }
        }

        private static void ComputeDiff(
            List<ManagedPlannedFile> plan,
            ManagedGenerationManifest active,
            ManagedPublishResult result)
        {
            var current = active?.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, ManagedManifestFile>(StringComparer.OrdinalIgnoreCase);
            var next = plan.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
            result.Added = next.Keys.Count(path => !current.ContainsKey(path));
            result.Changed = next.Count(pair => current.ContainsKey(pair.Key) &&
                !string.Equals(current[pair.Key].Sha256, pair.Value.Sha256, StringComparison.Ordinal));
            result.Removed = current.Keys.Count(path => !next.ContainsKey(path));
        }

        private static void EnsureWritableManagedRoot(string root)
        {
            if (!Directory.Exists(root))
            {
                throw new InvalidOperationException("Managed output root does not exist.");
            }

            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Managed output root cannot be a symbolic link.");
            }

            var probe = Path.Combine(root, ".m3u-editor-write-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }

        private static void EnsureNoReparsePoint(string root, string target)
        {
            var relative = target.Substring(root.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = root;
            foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((Directory.Exists(current) || File.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("Managed publication path escapes through a symbolic link.");
                }
            }
        }

        private static string CombineUnderRoot(string root, string relativePath)
        {
            var combined = Path.GetFullPath(Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Managed publication path is outside the output root.");
            }

            return combined;
        }

        private static string CombineRelative(string left, string right)
        {
            return left.Trim('/', '\\') + "/" + right.Trim('/', '\\');
        }

        private static string ComputeManagedHash(string content)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(content)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static ManagedGenerationManifest ReadManifest(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var manifest = JsonSerializer.Deserialize<ManagedGenerationManifest>(File.ReadAllText(path), JsonOptions);
            Guid mappingUuid;
            if (manifest == null || manifest.FormatVersion != 1 ||
                !Guid.TryParse(manifest.MappingUuid, out mappingUuid) ||
                !IsManagedHash(manifest.Revision) || manifest.Files == null ||
                manifest.Files.Any(file => file == null ||
                    !M3uEditorCatalogValidator.IsSafeRelativePath(file.RelativePath) ||
                    !IsManagedHash(file.Sha256) || file.Length < 0) ||
                manifest.Files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                    manifest.Files.Count)
            {
                throw new InvalidOperationException("Managed generation manifest is invalid.");
            }

            return manifest;
        }

        private static void WriteManifestAtomic(string path, ManagedGenerationManifest manifest)
        {
            var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null);
                return;
            }

            File.Move(temporaryPath, path);
        }

        private static bool ManifestFilesAreValid(string root, ManagedGenerationManifest manifest)
        {
            foreach (var file in manifest.Files)
            {
                if (!M3uEditorCatalogValidator.IsSafeRelativePath(file.RelativePath))
                {
                    return false;
                }

                var path = CombineUnderRoot(root, file.RelativePath);
                if (!File.Exists(path) || new FileInfo(path).Length != file.Length || !string.Equals(
                    ComputeManagedHash(File.ReadAllText(path, Encoding.UTF8)),
                    file.Sha256,
                    StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ManifestOwnsDirectory(string root, ManagedGenerationManifest manifest)
        {
            if (!ManifestFilesAreValid(root, manifest))
            {
                return false;
            }

            var expectedFiles = new HashSet<string>(
                manifest.Files.Select(file => CombineUnderRoot(root, file.RelativePath)),
                StringComparer.OrdinalIgnoreCase);
            var actualFiles = new HashSet<string>(
                Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);
            if (!expectedFiles.SetEquals(actualFiles))
            {
                return false;
            }

            var expectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in expectedFiles)
            {
                var directory = Path.GetDirectoryName(path);
                while (!string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
                {
                    expectedDirectories.Add(directory);
                    directory = Path.GetDirectoryName(directory);
                }
            }

            var actualDirectories = new HashSet<string>(
                Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);
            return expectedDirectories.SetEquals(actualDirectories) &&
                actualDirectories.All(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0);
        }

        private static bool IsManagedHash(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Length == 64 &&
                value.All(character => (character >= 'a' && character <= 'f') ||
                    (character >= '0' && character <= '9'));
        }

        private void MoveManifestFiles(
            string root,
            string destinationRoot,
            ManagedGenerationManifest manifest,
            string operation,
            List<string> movedFiles)
        {
            foreach (var file in manifest.Files)
            {
                var source = CombineUnderRoot(root, file.RelativePath);
                var destination = CombineUnderRoot(destinationRoot, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                InvokeManagedFileMove(operation, file.RelativePath);
                File.Move(source, destination);
                movedFiles.Add(file.RelativePath);
            }
        }

        private void RestoreMovedFiles(
            string root,
            string destinationRoot,
            List<string> movedFiles,
            string operation)
        {
            for (var index = movedFiles.Count - 1; index >= 0; index--)
            {
                var relativePath = movedFiles[index];
                var source = CombineUnderRoot(root, relativePath);
                var destination = CombineUnderRoot(destinationRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                InvokeManagedFileMove(operation, relativePath);
                File.Move(source, destination);
            }
        }

        private void RestoreManagedGeneration(
            string root,
            ManagedGenerationManifest active,
            ManagedGenerationManifest previous,
            string previousFilesRoot,
            string previousBackupRoot,
            List<string> quarantinedFiles,
            List<string> publishedFiles)
        {
            for (var index = publishedFiles.Count - 1; index >= 0; index--)
            {
                var relativePath = publishedFiles[index];
                var path = CombineUnderRoot(root, relativePath);
                InvokeManagedFileMove("remove-published", relativePath);
                File.Delete(path);
            }

            if (quarantinedFiles.Count > 0)
            {
                RestoreMovedFiles(previousFilesRoot, root, quarantinedFiles, "restore-quarantine");
            }

            if (Directory.Exists(previousFilesRoot))
            {
                DeleteEmptyDirectoryTree(previousFilesRoot);
            }

            if (!string.IsNullOrEmpty(previousBackupRoot) && Directory.Exists(previousBackupRoot))
            {
                Directory.Move(previousBackupRoot, previousFilesRoot);
            }

            var metadataRoot = Path.Combine(root, ManagedMetadataDirectoryName);
            var activeManifestPath = Path.Combine(metadataRoot, "active.json");
            var previousManifestPath = Path.Combine(metadataRoot, "previous.json");
            if (active == null)
            {
                File.Delete(activeManifestPath);
            }
            else
            {
                WriteManifestAtomic(activeManifestPath, active);
            }

            if (previous == null)
            {
                File.Delete(previousManifestPath);
            }
            else
            {
                WriteManifestAtomic(previousManifestPath, previous);
            }
        }

        private static void DeleteEmptyDirectoryTree(string root)
        {
            foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }

            if (Directory.EnumerateFileSystemEntries(root).Any())
            {
                throw new InvalidOperationException("Managed recovery encountered files not owned by this operation.");
            }

            Directory.Delete(root);
        }

        private static void DeleteOwnedDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static ManagedPublishResult Failed(
            string revision,
            string error,
            ManagedPublishResult existing = null)
        {
            var result = existing ?? new ManagedPublishResult();
            result.Success = false;
            result.Revision = revision;
            result.Error = error;
            return result;
        }

        private void EnsureCurrentSetupApproval(
            M3uEditorMapping mapping,
            string outputRoot,
            string approvedOutputRoots,
            PluginConfiguration setupConfig)
        {
            if (setupConfig == null)
            {
                return;
            }

            string expectedRoot;
            string currentRoot;
            string currentApprovedRoots;
            string error;
            if (!TryGetCanonicalSetupRoot(setupConfig, false, out expectedRoot, out error) ||
                !TryGetCurrentSetupRoot(
                    setupConfig,
                    mapping.IntegrationId,
                    expectedRoot,
                    approvedOutputRoots,
                    out currentRoot,
                    out currentApprovedRoots,
                    out error) ||
                !ManagedOutputPolicy.IsApproved(outputRoot, currentApprovedRoots, out error))
            {
                throw new InvalidOperationException(error);
            }
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

        private void InvokeManagedPhase(string phase)
        {
            ManagedPhaseHook?.Invoke(phase);
        }

        private void InvokeManagedFileMove(string operation, string relativePath)
        {
            ManagedFileMoveHook?.Invoke(operation, relativePath);
        }
    }
}
