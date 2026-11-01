using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Emby.M3uEditor.Plugin.Service
{
    internal sealed class ManagedPublishTransition
    {
        public int FormatVersion { get; set; } = 1;
        public string Root { get; set; }
        public string MappingUuid { get; set; }
        public string Id { get; set; }
        public string StagingDirectory { get; set; }
        public string Phase { get; set; } = "publishing";
        public ManagedGenerationManifest Active { get; set; }
        public ManagedGenerationManifest Previous { get; set; }
        public ManagedGenerationManifest Next { get; set; }
    }

    public partial class StrmSyncService
    {
        private const string ManagedTransitionFilename = "transition.json";

        // Called only under ManagedRootLocks, before validating the public generation.
        private void RecoverManagedTransition(string root, string mappingUuid)
        {
            var metadata = CombineUnderRoot(root, ManagedMetadataDirectoryName);
            var journal = CombineUnderRoot(metadata, ManagedTransitionFilename);
            EnsureNoReparsePoint(root, journal);
            if (!File.Exists(journal)) return; // Roots published by older versions need no migration.
            EnsureWritableManagedRoot(root);
            var transition = JsonSerializer.Deserialize<ManagedPublishTransition>(File.ReadAllText(journal), JsonOptions);
            ValidateManagedTransition(root, mappingUuid, transition);
            var staging = CombineUnderRoot(metadata, transition.StagingDirectory);
            var backup = CombineUnderRoot(metadata, "previous-backup-" + transition.Id);
            var previousFiles = CombineUnderRoot(metadata, "previous-files");
            var activePath = CombineUnderRoot(metadata, "active.json");
            var previousPath = CombineUnderRoot(metadata, "previous.json");
            foreach (var path in new[] { staging, backup, previousFiles, activePath, previousPath })
                EnsureNoReparsePoint(root, path);
            var active = ReadManifest(activePath);
            var previous = ReadManifest(previousPath);
            RequireRecovery(SameManagedManifest(active, transition.Active) || SameManagedManifest(active, transition.Next));
            RequireRecovery(SameManagedManifest(previous, transition.Previous) || SameManagedManifest(previous, transition.Active));
            ValidateRecoveryDirectory(staging, transition.Next);
            ValidateRecoveryDirectory(backup, transition.Previous);

            if (transition.Phase == "publishing" && SameManagedManifest(active, transition.Next))
            {
                RequireRecovery(RecoveryManifestValid(root, transition.Next));
                RequireRecovery(SameManagedManifest(previous, transition.Active));
                RequireRecovery(transition.Active == null || RecoveryManifestValid(previousFiles, transition.Active));
                ValidateRecoveryDirectory(previousFiles, transition.Active);
                transition.Phase = "committed";
                WriteManagedJsonAtomic(journal, transition);
            }

            if (transition.Phase == "publishing" || transition.Phase == "restoring")
            {
                // A still lives in previous-files until the first directory rename. B then
                // occupies that directory; the persisted phase keeps retries unambiguous.
                var oldPreviousInPlace = transition.Previous != null && !Directory.Exists(backup);
                if (oldPreviousInPlace)
                {
                    ValidateRecoveryDirectory(previousFiles, transition.Previous);
                    RequireRecovery(RecoveryManifestValid(previousFiles, transition.Previous));
                }
                else
                {
                    ValidateRecoveryDirectory(previousFiles, transition.Active);
                    RequireRecovery(transition.Previous == null || RecoveryManifestValid(backup, transition.Previous));
                }
                ValidateRecoveryPublicFiles(root, oldPreviousInPlace ? null : previousFiles, transition);
                transition.Phase = "restoring";
                WriteManagedJsonAtomic(journal, transition);
                if (oldPreviousInPlace) Directory.Move(previousFiles, backup);

                foreach (var file in transition.Next.Files)
                {
                    var path = CombineUnderRoot(root, file.RelativePath);
                    var old = transition.Active?.Files.FirstOrDefault(value => value.RelativePath == file.RelativePath);
                    var quarantined = old != null && RecoveryFileMatches(CombineUnderRoot(previousFiles, old.RelativePath), old);
                    if (File.Exists(path) && (old == null || quarantined))
                    {
                        InvokeManagedFileMove("recovery-remove", file.RelativePath);
                        File.Delete(path);
                    }
                }
                foreach (var file in transition.Active?.Files ?? new List<ManagedManifestFile>())
                {
                    var source = CombineUnderRoot(previousFiles, file.RelativePath);
                    var destination = CombineUnderRoot(root, file.RelativePath);
                    if (!File.Exists(source)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    InvokeManagedFileMove("recovery-restore", file.RelativePath);
                    File.Move(source, destination);
                }
                RequireRecovery(transition.Active == null || RecoveryManifestValid(root, transition.Active));
                DeleteRecoveryDirectory(previousFiles, transition.Active);
                transition.Phase = "restored";
                WriteManagedJsonAtomic(journal, transition);
                InvokeManagedPhase("after-recovery-files");
            }

            if (transition.Phase == "restored")
            {
                RequireRecovery(transition.Active == null || RecoveryManifestValid(root, transition.Active));
                if (Directory.Exists(backup))
                {
                    ValidateRecoveryDirectory(previousFiles, null);
                    ValidateRecoveryDirectory(backup, transition.Previous);
                    RequireRecovery(RecoveryManifestValid(backup, transition.Previous));
                    DeleteRecoveryDirectory(previousFiles, null);
                    Directory.Move(backup, previousFiles);
                    InvokeManagedPhase("after-recovery-previous");
                }
                ValidateRecoveryDirectory(previousFiles, transition.Previous);
                RequireRecovery(transition.Previous == null || RecoveryManifestValid(previousFiles, transition.Previous));
                if (transition.Previous == null) DeleteRecoveryDirectory(previousFiles, null);
                RestoreRecoveryManifest(activePath, transition.Active);
                RestoreRecoveryManifest(previousPath, transition.Previous);
                InvokeManagedPhase("after-recovery-manifests");
            }
            else
            {
                RequireRecovery(transition.Phase == "committed");
                RequireRecovery(SameManagedManifest(active, transition.Next) && RecoveryManifestValid(root, transition.Next));
                RequireRecovery(SameManagedManifest(previous, transition.Active));
                ValidateRecoveryDirectory(previousFiles, transition.Active);
                RequireRecovery(transition.Active == null || RecoveryManifestValid(previousFiles, transition.Active));
            }

            // Validate all remaining evidence before deleting any of it. Delete only named,
            // hash-matching files, never a recursive directory containing unknown content.
            ValidateRecoveryDirectory(staging, transition.Next);
            ValidateRecoveryDirectory(backup, transition.Previous);
            DeleteRecoveryDirectory(staging, transition.Next);
            DeleteRecoveryDirectory(backup, transition.Previous);
            InvokeManagedPhase("before-recovery-complete");
            File.Delete(journal); // Last: a crash during cleanup must still be recoverable.
        }

        private static void ValidateManagedTransition(string root, string mappingUuid, ManagedPublishTransition value)
        {
            Guid id;
            RequireRecovery(value != null && value.FormatVersion == 1 &&
                string.Equals(value.Root, root, PathComparison) &&
                string.Equals(value.MappingUuid, mappingUuid, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(value.Id, "N", out id) &&
                value.StagingDirectory != null && value.StagingDirectory.StartsWith("staging-", StringComparison.Ordinal) &&
                Guid.TryParseExact(value.StagingDirectory.Substring(8), "N", out id) &&
                new[] { "publishing", "restoring", "restored", "committed" }.Contains(value.Phase));
            foreach (var manifest in new[] { value.Active, value.Previous, value.Next }.Where(manifest => manifest != null))
            {
                ValidateManagedManifest(manifest);
                RequireRecovery(string.Equals(manifest.MappingUuid, mappingUuid, StringComparison.OrdinalIgnoreCase));
                RequireRecovery(manifest.Files.All(file => !file.RelativePath.Split('/','\\')
                    .Any(segment => segment.Equals(ManagedMetadataDirectoryName, StringComparison.OrdinalIgnoreCase))));
            }
            RequireRecovery(value.Next != null && (value.Previous == null || value.Active != null));
        }

        private static bool SameManagedManifest(ManagedGenerationManifest left, ManagedGenerationManifest right)
        {
            return JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions);
        }

        private static bool RecoveryFileMatches(string path, ManagedManifestFile file)
        {
            return File.Exists(path) && new FileInfo(path).Length == file.Length &&
                ComputeManagedHash(File.ReadAllText(path, Encoding.UTF8)) == file.Sha256;
        }

        private static bool RecoveryManifestValid(string root, ManagedGenerationManifest manifest)
        {
            foreach (var file in manifest.Files)
            {
                var path = CombineUnderRoot(root, file.RelativePath);
                EnsureNoReparsePoint(root, path);
                if (!RecoveryFileMatches(path, file)) return false;
            }
            return true;
        }

        private static void ValidateRecoveryPublicFiles(string root, string quarantine, ManagedPublishTransition value)
        {
            foreach (var relative in (value.Active?.Files ?? new List<ManagedManifestFile>()).Concat(value.Next.Files)
                .Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var path = CombineUnderRoot(root, relative);
                EnsureNoReparsePoint(root, path);
                var old = value.Active?.Files.FirstOrDefault(file => file.RelativePath == relative);
                var next = value.Next.Files.FirstOrDefault(file => file.RelativePath == relative);
                var saved = old != null && quarantine != null && RecoveryFileMatches(CombineUnderRoot(quarantine, relative), old);
                RequireRecovery(old == null || saved || RecoveryFileMatches(path, old));
                if (File.Exists(path))
                    RequireRecovery((old != null && !saved && RecoveryFileMatches(path, old)) ||
                        (next != null && RecoveryFileMatches(path, next) && (old == null || saved)));
            }
        }

        private static void ValidateRecoveryDirectory(string root, ManagedGenerationManifest manifest)
        {
            RequireRecovery(!File.Exists(root));
            if (!Directory.Exists(root)) return;
            var files = (manifest?.Files ?? new List<ManagedManifestFile>())
                .ToDictionary(file => CombineUnderRoot(root, file.RelativePath), file => file, StringComparer.Ordinal);
            var directories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in files.Keys) AddExpectedParentDirectories(directories, Path.GetDirectoryName(path), root);
            ValidateRecoveryTree(root, files, directories);
        }

        private static void ValidateRecoveryTree(string directory, Dictionary<string, ManagedManifestFile> files, HashSet<string> directories)
        {
            RequireRecovery((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                RequireRecovery((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0);
                if (Directory.Exists(path))
                {
                    RequireRecovery(directories.Contains(path));
                    ValidateRecoveryTree(path, files, directories);
                }
                else
                {
                    ManagedManifestFile file;
                    RequireRecovery(files.TryGetValue(path, out file) && RecoveryFileMatches(path, file));
                }
            }
        }

        private void DeleteRecoveryDirectory(string root, ManagedGenerationManifest manifest)
        {
            if (!Directory.Exists(root)) return;
            foreach (var file in manifest?.Files ?? new List<ManagedManifestFile>())
            {
                var path = CombineUnderRoot(root, file.RelativePath);
                if (File.Exists(path))
                {
                    InvokeManagedFileMove("recovery-cleanup", file.RelativePath);
                    File.Delete(path);
                }
            }
            foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
                Directory.Delete(directory);
            Directory.Delete(root);
        }

        private static void RestoreRecoveryManifest(string path, ManagedGenerationManifest manifest)
        {
            if (manifest == null) File.Delete(path);
            else if (!SameManagedManifest(ReadManifest(path), manifest)) WriteManifestAtomic(path, manifest);
        }

        private static void RequireRecovery(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Managed publication recovery metadata or owned files are invalid.");
        }
    }
}
