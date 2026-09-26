using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Emby.M3uEditor.Plugin.Api;

namespace Emby.M3uEditor.Plugin.Service
{
    internal sealed class ManagedDirectoryOwnership
    {
        public int FormatVersion { get; set; } = 1;
        public string Kind { get; set; }
        public int IntegrationId { get; set; }
        public string OperationId { get; set; }
        public string MappingUuid { get; set; }
        public string Name { get; set; }
        public string CollectionType { get; set; }
        public string Path { get; set; }
        public string State { get; set; }
    }

    internal sealed class ManagedLibraryProvisioningService
    {
        internal const int ApiVersion = 1;
        private static readonly ConcurrentDictionary<string, object> Gates =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        private readonly string _ownerPath;

        internal ManagedLibraryProvisioningService(string ownerPath)
        {
            _ownerPath = ownerPath;
        }

        internal ManagedLibraryOperationResult Prepare(PluginConfiguration config, int integrationId,
            string operationId, string name, string collectionType, Action saveConfiguration)
        {
            Guid parsedOperation;
            string normalizedName;
            string root;
            string error;
            if (!TryValidateRequest(config, integrationId, operationId, name, collectionType,
                out parsedOperation, out normalizedName, out root, out error))
            {
                return Failed(integrationId, operationId, "validation", error);
            }

            var canonicalOperation = parsedOperation.ToString("D");
            var target = BuildExpectedPath(root, normalizedName, collectionType);
            var gate = Gates.GetOrAdd(root, _ => new object());
            lock (gate)
            {
                List<ManagedDirectoryOwnership> records;
                if (!TryReadOwnership(config, root, out records, out error))
                    return Failed(integrationId, canonicalOperation, "state", error);

                var operationRecord = records.FirstOrDefault(record =>
                    string.Equals(record.OperationId, canonicalOperation, StringComparison.OrdinalIgnoreCase));
                if (operationRecord != null)
                {
                    if (!LibraryRecordMatches(operationRecord, integrationId, normalizedName, collectionType, target) ||
                        !Directory.Exists(target) || ManagedOutputPolicy.HasReparsePointInPath(target))
                        return Failed(integrationId, canonicalOperation, "conflict",
                            "The managed library operation conflicts with existing ownership.");
                    return Success(operationRecord, true);
                }

                if (records.Any(record => PathsEqual(record.Path, target)) || Directory.Exists(target) || File.Exists(target))
                    return Failed(integrationId, canonicalOperation, "collision",
                        "The managed library destination already exists and is not owned by this operation.");
                if (!Directory.Exists(root) || ManagedOutputPolicy.HasReparsePointInPath(root) ||
                    !ManagedOutputPolicy.IsLocallyWritableRoot(root))
                    return Failed(integrationId, canonicalOperation, "filesystem",
                        "The managed library parent is not safely writable.");

                var staging = Path.Combine(root, ".managed-prepare-" + canonicalOperation);
                if (Directory.Exists(staging) || File.Exists(staging))
                    return Failed(integrationId, canonicalOperation, "collision",
                        "The managed library operation has an unresolved filesystem collision.");

                var original = config.ManagedDirectoryOwnershipJson;
                var moved = false;
                try
                {
                    Directory.CreateDirectory(staging);
                    Directory.Move(staging, target);
                    moved = true;
                    var record = new ManagedDirectoryOwnership
                    {
                        Kind = "library", IntegrationId = integrationId, OperationId = canonicalOperation,
                        Name = normalizedName, CollectionType = collectionType, Path = target, State = "prepared"
                    };
                    records.Add(record);
                    config.ManagedDirectoryOwnershipJson = JsonSerializer.Serialize(records, JsonOptions);
                    saveConfiguration?.Invoke();
                    return Success(record, false);
                }
                catch (Exception ex) when (IsExpectedException(ex))
                {
                    config.ManagedDirectoryOwnershipJson = original;
                    DeleteIfEmpty(staging);
                    if (moved) DeleteIfEmpty(target);
                    return Failed(integrationId, canonicalOperation, "filesystem",
                        "The managed library directory could not be safely prepared and persisted.");
                }
                catch
                {
                    config.ManagedDirectoryOwnershipJson = original;
                    DeleteIfEmpty(staging);
                    if (moved) DeleteIfEmpty(target);
                    throw;
                }
            }
        }

        internal ManagedLibraryOperationResult Commit(PluginConfiguration config, int integrationId,
            string operationId, Action saveConfiguration)
        {
            return ChangeState(config, integrationId, operationId, false, saveConfiguration);
        }

        internal ManagedLibraryOperationResult Abort(PluginConfiguration config, int integrationId,
            string operationId, Action saveConfiguration)
        {
            return ChangeState(config, integrationId, operationId, true, saveConfiguration);
        }

        internal string GetExpectedLibraryPath(PluginConfiguration config, string name, string collectionType)
        {
            var setup = new ManagedSetupService(_ownerPath).Get(config);
            return setup.Ready ? BuildExpectedPath(setup.ConfirmedRoot, name, collectionType) : null;
        }

        internal static bool TryProvisionMapping(string ownerPath, PluginConfiguration config,
            int integrationId, string mappingUuid, string targetPath, Action saveConfiguration, out string error)
        {
            error = "The missing mapping directory is not below an owned committed library.";
            Guid parsedMapping;
            var setup = new ManagedSetupService(ownerPath).Get(config);
            if (saveConfiguration == null || !setup.Ready || setup.IntegrationId != integrationId || !Guid.TryParse(mappingUuid, out parsedMapping) ||
                !ManagedOutputPolicy.IsApproved(targetPath, config.ManagedApprovedOutputRoots, out var approvalError) ||
                File.Exists(targetPath) || ManagedOutputPolicy.HasReparsePointInPath(targetPath))
                return false;

            List<ManagedDirectoryOwnership> records;
            string stateError;
            if (!TryReadOwnership(config, setup.ConfirmedRoot, out records, out stateError))
            {
                error = stateError;
                return false;
            }
            var parent = records.SingleOrDefault(record => record.Kind == "library" && record.State == "committed" &&
                ManagedOutputPolicy.IsDirectChild(record.Path, targetPath));
            if (parent == null || !Directory.Exists(parent.Path) || ManagedOutputPolicy.HasReparsePointInPath(parent.Path))
                return false;

            var canonicalMapping = parsedMapping.ToString("D");
            var existing = records.FirstOrDefault(record => record.Kind == "mapping" &&
                (string.Equals(record.MappingUuid, canonicalMapping, StringComparison.OrdinalIgnoreCase) ||
                 PathsEqual(record.Path, targetPath)));
            if (existing != null)
            {
                if (!string.Equals(existing.MappingUuid, canonicalMapping, StringComparison.OrdinalIgnoreCase) ||
                    !PathsEqual(existing.Path, targetPath) || existing.IntegrationId != integrationId)
                {
                    error = "The mapping directory conflicts with existing plugin ownership.";
                    return false;
                }
                if (Directory.Exists(targetPath))
                {
                    error = string.Empty;
                    return true;
                }
            }
            else if (Directory.Exists(targetPath))
            {
                error = "The mapping directory already exists without plugin ownership.";
                return false;
            }

            var staging = Path.Combine(parent.Path, ".managed-mapping-" + canonicalMapping);
            var original = config.ManagedDirectoryOwnershipJson;
            var moved = false;
            try
            {
                if (Directory.Exists(staging) || File.Exists(staging) ||
                    !ManagedOutputPolicy.IsLocallyWritableRoot(parent.Path)) return false;
                Directory.CreateDirectory(staging);
                Directory.Move(staging, targetPath);
                moved = true;
                if (existing == null)
                {
                    records.Add(new ManagedDirectoryOwnership
                    {
                        Kind = "mapping", IntegrationId = integrationId, MappingUuid = canonicalMapping,
                        Path = targetPath, State = "committed"
                    });
                }
                config.ManagedDirectoryOwnershipJson = JsonSerializer.Serialize(records, JsonOptions);
                saveConfiguration?.Invoke();
                error = string.Empty;
                return true;
            }
            catch (Exception ex) when (IsExpectedException(ex))
            {
                config.ManagedDirectoryOwnershipJson = original;
                DeleteIfEmpty(staging);
                if (moved) DeleteIfEmpty(targetPath);
                error = "The mapping directory could not be safely provisioned and persisted.";
                return false;
            }
            catch
            {
                config.ManagedDirectoryOwnershipJson = original;
                DeleteIfEmpty(staging);
                if (moved) DeleteIfEmpty(targetPath);
                throw;
            }
        }

        internal static bool TryReadOwnership(PluginConfiguration config, string canonicalRoot,
            out List<ManagedDirectoryOwnership> records, out string error)
        {
            records = new List<ManagedDirectoryOwnership>();
            error = "Managed directory ownership state is invalid.";
            try
            {
                if (!string.IsNullOrWhiteSpace(config.ManagedDirectoryOwnershipJson))
                    records = JsonSerializer.Deserialize<List<ManagedDirectoryOwnership>>(
                        config.ManagedDirectoryOwnershipJson, JsonOptions);
            }
            catch (JsonException) { return false; }
            catch (NotSupportedException) { return false; }

            if (records == null || records.Any(record => record == null || record.FormatVersion != 1 ||
                (record.Kind != "library" && record.Kind != "mapping") || record.IntegrationId < 1 ||
                string.IsNullOrEmpty(record.Path)) ||
                records.GroupBy(record => record.Path, PathComparer).Any(group => group.Count() > 1))
            {
                records = null;
                return false;
            }
            foreach (var record in records)
            {
                var validLibrary = record.Kind == "library" && ManagedOutputPolicy.IsDirectChild(canonicalRoot, record.Path);
                var validMapping = record.Kind == "mapping" && records.Any(parent => parent.Kind == "library" &&
                    parent.State == "committed" && ManagedOutputPolicy.IsDirectChild(parent.Path, record.Path));
                if (!validLibrary && !validMapping)
                {
                    records = null;
                    return false;
                }
            }
            error = string.Empty;
            return true;
        }

        private ManagedLibraryOperationResult ChangeState(PluginConfiguration config, int integrationId,
            string operationId, bool abort, Action saveConfiguration)
        {
            Guid parsed;
            var setup = new ManagedSetupService(_ownerPath).Get(config);
            if (!setup.Ready || integrationId != setup.IntegrationId || !Guid.TryParse(operationId, out parsed))
                return Failed(integrationId, operationId, "validation", "The managed library operation is invalid.");

            var canonicalOperation = parsed.ToString("D");
            var gate = Gates.GetOrAdd(setup.ConfirmedRoot, _ => new object());
            lock (gate)
            {
                List<ManagedDirectoryOwnership> records;
                string error;
                if (!TryReadOwnership(config, setup.ConfirmedRoot, out records, out error))
                    return Failed(integrationId, canonicalOperation, "state", error);
                var record = records.FirstOrDefault(value => value.Kind == "library" &&
                    string.Equals(value.OperationId, canonicalOperation, StringComparison.OrdinalIgnoreCase) &&
                    value.IntegrationId == integrationId);
                if (record == null || !Directory.Exists(record.Path) || ManagedOutputPolicy.HasReparsePointInPath(record.Path))
                    return Failed(integrationId, canonicalOperation, "ownership",
                        "The managed library operation is not owned by this setup.");
                if (!abort && record.State == "committed") return Success(record, true);
                if (abort && record.State != "prepared")
                    return Failed(integrationId, canonicalOperation, "state", "Only a prepared library can be aborted.");
                if (abort && records.Any(value => value.Kind == "mapping" &&
                    ManagedOutputPolicy.IsDirectChild(record.Path, value.Path)))
                    return Failed(integrationId, canonicalOperation, "state",
                        "A library with owned mapping directories cannot be aborted.");

                var original = config.ManagedDirectoryOwnershipJson;
                try
                {
                    if (abort)
                    {
                        if (Directory.EnumerateFileSystemEntries(record.Path).Any())
                            return Failed(integrationId, canonicalOperation, "not_empty",
                                "The prepared library is not empty and cannot be aborted.");
                        Directory.Delete(record.Path, false);
                        records.Remove(record);
                    }
                    else record.State = "committed";
                    config.ManagedDirectoryOwnershipJson = JsonSerializer.Serialize(records, JsonOptions);
                    saveConfiguration?.Invoke();
                    if (abort)
                        return new ManagedLibraryOperationResult { CapabilityVersion = ApiVersion,
                            IntegrationId = integrationId, OperationId = canonicalOperation, State = "aborted", Success = true };
                    return Success(record, false);
                }
                catch (Exception ex) when (IsExpectedException(ex))
                {
                    config.ManagedDirectoryOwnershipJson = original;
                    return Failed(integrationId, canonicalOperation, "persistence",
                        "The managed library operation could not be persisted.");
                }
                catch
                {
                    config.ManagedDirectoryOwnershipJson = original;
                    throw;
                }
            }
        }

        private bool TryValidateRequest(PluginConfiguration config, int integrationId, string operationId,
            string name, string collectionType, out Guid operation, out string normalizedName,
            out string root, out string error)
        {
            operation = Guid.Empty;
            normalizedName = null;
            root = null;
            error = "The managed library request is invalid.";
            var setup = new ManagedSetupService(_ownerPath).Get(config);
            if (!setup.Ready || integrationId != setup.IntegrationId || !Guid.TryParse(operationId, out operation) ||
                string.IsNullOrWhiteSpace(name) || name.Length > 255 || !string.Equals(name, name.Trim(), StringComparison.Ordinal) ||
                name.Any(char.IsControl) || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains("..") ||
                (collectionType != "movies" && collectionType != "tvshows")) return false;
            normalizedName = name.Normalize(NormalizationForm.FormKC);
            if (normalizedName.Length > 255) return false;
            root = setup.ConfirmedRoot;
            error = string.Empty;
            return true;
        }

        private static string BuildExpectedPath(string root, string name, string collectionType)
        {
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(name.Normalize(NormalizationForm.FormKC))))
                    .Replace("-", string.Empty).ToLowerInvariant().Substring(0, 24);
                string path;
                if (!ManagedOutputPolicy.TryJoinUnderRoot(root, collectionType + "-" + hash, out path))
                    throw new InvalidOperationException("The managed library destination is invalid.");
                return path;
            }
        }

        private static bool LibraryRecordMatches(ManagedDirectoryOwnership record, int integrationId,
            string name, string collectionType, string path)
        {
            return record.Kind == "library" && record.IntegrationId == integrationId &&
                string.Equals(record.Name, name, StringComparison.Ordinal) &&
                string.Equals(record.CollectionType, collectionType, StringComparison.Ordinal) && PathsEqual(record.Path, path) &&
                (record.State == "prepared" || record.State == "committed");
        }

        private static ManagedLibraryOperationResult Success(ManagedDirectoryOwnership record, bool duplicate)
        {
            return new ManagedLibraryOperationResult { CapabilityVersion = ApiVersion, IntegrationId = record.IntegrationId,
                OperationId = record.OperationId, PreparedPath = record.Path, State = record.State,
                Success = true, Duplicate = duplicate };
        }

        private static ManagedLibraryOperationResult Failed(int integrationId, string operationId,
            string errorClass, string message)
        {
            return new ManagedLibraryOperationResult { CapabilityVersion = ApiVersion, IntegrationId = integrationId,
                OperationId = operationId, Success = false, ErrorClass = errorClass, Message = message };
        }

        private static bool IsExpectedException(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                ex is NotSupportedException || ex is InvalidOperationException || ex is JsonException;
        }

        private static void DeleteIfEmpty(string path)
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }

        private static bool PathsEqual(string left, string right) => string.Equals(left, right, PathComparison);
        private static StringComparer PathComparer => Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private static StringComparison PathComparison => Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }
}
