using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Tests.Fakes;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedCrashRecoveryTests
    {
        [Theory]
        [InlineData("after-quarantine")]
        [InlineData("after-publish")]
        [InlineData("before-manifest")]
        [InlineData("quarantine")]
        [InlineData("publish")]
        [InlineData("after-previous-backup")]
        [InlineData("after-manifest")]
        [InlineData("after-journal")]
        [InlineData("recovery-cleanup")]
        public async Task FreshProcesses_RecoverAndPublishThenRollback(string phase)
        {
            using var temp = new TempDirectory();
            await Run("setup", temp.Path);
            await Run("crash", temp.Path, phase, 73);
            await Run("publish", temp.Path);
            var before = Snapshot(temp.Path);
            await Run("publish", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
            await Run("rollback", temp.Path);
            ManagedCrashProcess.Verify(temp.Path);
            Assert.Contains(new string('b', 64), File.ReadAllText(Path.Combine(temp.Path, ".m3u-editor-for-emby/active.json")));
        }

        [Theory]
        [InlineData("after-quarantine")]
        [InlineData("before-manifest")]
        public async Task FreshReconcile_RecoversBeforePublishingAndReporting(string phase)
        {
            using var temp = new TempDirectory();
            await Run("setup", temp.Path);
            await Run("crash", temp.Path, phase, 73);
            await Run("reconcile", temp.Path);
            ManagedCrashProcess.Verify(temp.Path);
            await Run("rollback", temp.Path);
        }

        [Fact]
        public async Task FreshRollback_RecoversPriorGenerationBeforeRollingBack()
        {
            using var temp = new TempDirectory();
            await Run("setup", temp.Path);
            await Run("crash", temp.Path, "after-quarantine", 73);
            await Run("rollback", temp.Path);
            Assert.Contains(new string('a', 64), File.ReadAllText(Path.Combine(temp.Path, ".m3u-editor-for-emby/active.json")));
        }

        [Theory]
        [InlineData("recovery-remove")]
        [InlineData("recovery-restore")]
        [InlineData("after-recovery-files")]
        [InlineData("after-recovery-previous")]
        [InlineData("after-recovery-manifests")]
        [InlineData("before-recovery-complete")]
        public async Task RecoveryCanItselfCrashAndRestart(string phase)
        {
            using var temp = new TempDirectory();
            await Run("setup", temp.Path);
            var before = Snapshot(temp.Path);
            await Run("crash", temp.Path, "after-publish", 73);
            await Run("recover-crash", temp.Path, phase, 73);
            await Run("duplicate", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
            await Run("duplicate", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
        }

        [Theory]
        [InlineData("truncated")]
        [InlineData("mapping")]
        [InlineData("manifest-mapping")]
        [InlineData("phase")]
        [InlineData("missing-next")]
        [InlineData("duplicate-path")]
        [InlineData("root")]
        [InlineData("traversal")]
        [InlineData("metadata-path")]
        [InlineData("staging")]
        [InlineData("hash")]
        [InlineData("foreign-quarantine")]
        [InlineData("foreign-backup")]
        [InlineData("foreign-staging")]
        [InlineData("foreign-empty-directory")]
        [InlineData("foreign-public")]
        [InlineData("symlink")]
        public async Task InvalidRecoveryEvidence_FailsClosedWithoutChangingAnyFiles(string damage)
        {
            using var temp = new TempDirectory();
            using var foreign = new TempDirectory();
            await Run("setup", temp.Path);
            await Run("crash", temp.Path, "after-quarantine", 73);
            var metadata = Path.Combine(temp.Path, ".m3u-editor-for-emby");
            var journal = Path.Combine(metadata, "transition.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(journal));
            switch (damage)
            {
                case "truncated": File.WriteAllText(journal, "{\"FormatVersion\":"); break;
                case "mapping": json["MappingUuid"] = Guid.NewGuid().ToString(); break;
                case "manifest-mapping": json["Next"]["MappingUuid"] = Guid.NewGuid().ToString(); break;
                case "phase": json["Phase"] = "unknown"; break;
                case "missing-next": json["Next"] = null; break;
                case "duplicate-path": json["Next"]["Files"][1]["RelativePath"] = json["Next"]["Files"][0]["RelativePath"].GetValue<string>(); break;
                case "root": json["Root"] = foreign.Path; break;
                case "traversal": json["Next"]["Files"][0]["RelativePath"] = "../foreign.sentinel"; break;
                case "metadata-path": json["Next"]["Files"][0]["RelativePath"] = ".m3u-editor-for-emby/active.json"; break;
                case "staging": json["StagingDirectory"] = "../foreign"; break;
                case "hash": json["Next"]["Files"][0]["Sha256"] = new string('0', 64); break;
                case "foreign-quarantine": File.WriteAllText(Path.Combine(metadata, "previous-files/foreign.sentinel"), "foreign"); break;
                case "foreign-backup": File.WriteAllText(Path.Combine(Directory.GetDirectories(metadata, "previous-backup-*").Single(), "foreign.sentinel"), "foreign"); break;
                case "foreign-staging": File.WriteAllText(Path.Combine(Directory.GetDirectories(metadata, "staging-*").Single(), "foreign.sentinel"), "foreign"); break;
                case "foreign-empty-directory": Directory.CreateDirectory(Path.Combine(metadata, "previous-files/unowned")); break;
                case "foreign-public": File.WriteAllText(Path.Combine(temp.Path, "Synthetic/movie.nfo"), "foreign"); break;
                case "symlink":
                    File.WriteAllText(Path.Combine(foreign.Path, "foreign.sentinel"), "foreign");
                    Directory.CreateSymbolicLink(Path.Combine(metadata, "previous-files/link"), foreign.Path);
                    break;
            }
            if (damage != "truncated") File.WriteAllText(journal, json.ToJsonString());
            var before = Snapshot(temp.Path);
            var outside = Snapshot(foreign.Path);
            await Run("reject", temp.Path);
            await Run("reject-rollback", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
            Assert.Equal(outside, Snapshot(foreign.Path));
            if (damage == "foreign-empty-directory") Assert.True(Directory.Exists(Path.Combine(metadata, "previous-files/unowned")));
        }

        [Theory]
        [InlineData("reject-duplicate")]
        [InlineData("reject-rollback")]
        public async Task CaseOnlyTransition_ModifiedNextFileRejectsWithoutAnyMutation(string operation)
        {
            using var temp = new TempDirectory();
            await Run("setup", temp.Path);
            await Run("case-crash", temp.Path, "after-publish", 73);
            var nextFile = Path.Combine(temp.Path, "synthetic/movie.nfo");
            Assert.True(File.Exists(nextFile));
            File.WriteAllText(nextFile, "FOREIGN MODIFIED CONTENT");
            var before = Snapshot(temp.Path);
            var directories = Directory.GetDirectories(temp.Path, "*", SearchOption.AllDirectories).OrderBy(path => path).ToArray();
            await Run(operation, temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
            Assert.Equal(directories, Directory.GetDirectories(temp.Path, "*", SearchOption.AllDirectories).OrderBy(path => path).ToArray());
            await Run(operation, temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
            Assert.True(File.Exists(Path.Combine(temp.Path, ".m3u-editor-for-emby/transition.json")));
        }

        [Theory]
        [InlineData("after-quarantine")]
        [InlineData("after-publish")]
        [InlineData("before-manifest")]
        public async Task CaseOnlyTransition_ValidRecoveryPreservesActiveAndPrevious(string phase)
        {
            using var temp = new TempDirectory();
            await Run("setup", temp.Path);
            var before = Snapshot(temp.Path);
            await Run("case-crash", temp.Path, phase, 73);
            await Run("duplicate", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
            await Run("duplicate", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
            await Run("rollback", temp.Path);
            Assert.Contains(new string('a', 64), File.ReadAllText(Path.Combine(temp.Path, ".m3u-editor-for-emby/active.json")));
        }

        [Theory]
        [InlineData("after-stage")]
        [InlineData("stage")]
        [InlineData("during-stage-write")]
        public async Task HardKill_InitialPreparation_AllowsFreshRetry(string phase)
        {
            using var control = new TempDirectory();
            await Run("initial", control.Path);
            using var temp = new TempDirectory();
            await KillAt("hard-publish", temp.Path, phase);
            Assert.False(File.Exists(Path.Combine(temp.Path, ".m3u-editor-for-emby/active.json")));
            Assert.NotEmpty(Directory.GetFiles(temp.Path, "*.strm", SearchOption.AllDirectories));
            await Run("initial", temp.Path);
            var before = Snapshot(temp.Path);
            await Run("initial", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
        }

        [Theory]
        [InlineData("rollback-active")]
        [InlineData("rollback-previous")]
        [InlineData("after-rollback-files")]
        [InlineData("before-rollback-swap")]
        [InlineData("after-rollback-directory-swap")]
        [InlineData("after-rollback-active-manifest")]
        [InlineData("after-rollback-previous-manifest")]
        [InlineData("before-rollback-recovery-complete")]
        public async Task HardKill_Rollback_AllowsFreshRollbackPublishAndRollback(string phase)
        {
            using var control = new TempDirectory();
            await Run("setup", control.Path);
            await Run("rollback", control.Path);
            using var temp = new TempDirectory();
            await Run("setup", temp.Path);
            await KillAt("hard-rollback", temp.Path, phase);
            var scratch = Path.Combine(temp.Path, ".m3u-editor-for-emby/rollback-current");
            if (phase == "rollback-active" || phase == "rollback-previous")
                Assert.NotEmpty(Directory.GetFiles(scratch, "*", SearchOption.AllDirectories));
            await Run("rollback", temp.Path);
            await Run("publish", temp.Path);
            var before = Snapshot(temp.Path);
            await Run("publish", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
            await Run("rollback", temp.Path);
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(temp.Path, "foreign.sentinel")));
        }

        [Theory]
        [InlineData("after-stage")]
        [InlineData("stage")]
        [InlineData("during-stage-write")]
        public async Task HardKill_RepeatedPreparation_PreservesPairAndBoundsStaging(string phase)
        {
            using var temp = new TempDirectory();
            await Run("setup", temp.Path);
            var before = Snapshot(temp.Path);
            for (var cycle = 0; cycle < 3; cycle++)
            {
                await KillAt("hard-publish", temp.Path, phase);
                ManagedCrashProcess.Verify(temp.Path);
                await Run("duplicate", temp.Path);
                Assert.Equal(before, Snapshot(temp.Path));
                Assert.Empty(Directory.GetDirectories(Path.Combine(temp.Path, ".m3u-editor-for-emby"), "staging-*"));
            }
            await Run("publish", temp.Path);
            await Run("rollback", temp.Path);
        }

        [Theory]
        [InlineData("rollback-recovery-previous")]
        [InlineData("rollback-recovery-active")]
        [InlineData("after-rollback-recovery-files")]
        [InlineData("before-rollback-recovery-complete")]
        public async Task HardKill_RollbackRecovery_ReplaysIdempotently(string phase)
        {
            using var temp = new TempDirectory();
            await Run("setup", temp.Path);
            var before = Snapshot(temp.Path);
            // All previous files reached the public root, but swap intent is not yet committed.
            await KillAt("hard-rollback", temp.Path, "after-rollback-files");
            await KillAt("hard-recover", temp.Path, phase);
            await Run("duplicate", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
            await Run("duplicate", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
            await Run("rollback", temp.Path);
        }

        [Theory]
        [InlineData("after-rollback-directory-swap")]
        [InlineData("after-rollback-active-manifest")]
        [InlineData("after-rollback-previous-manifest")]
        public async Task HardKill_RollbackSwapRecovery_CanItselfRestart(string phase)
        {
            using var temp = new TempDirectory();
            await Run("setup", temp.Path);
            await KillAt("hard-rollback", temp.Path, "before-rollback-swap");
            await KillAt("hard-recover", temp.Path, phase);
            await Run("publish", temp.Path);
            var before = Snapshot(temp.Path);
            await Run("publish", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
            await Run("rollback", temp.Path);
        }

        [Theory]
        [InlineData("preparation", "foreign")]
        [InlineData("preparation", "modified")]
        [InlineData("partial", "modified")]
        [InlineData("preparation", "root")]
        [InlineData("preparation", "mapping")]
        [InlineData("preparation", "manifest")]
        [InlineData("preparation", "symlink")]
        [InlineData("preparation", "intent-content")]
        [InlineData("preparation", "intent-path")]
        [InlineData("preparation", "intent-symlink")]
        [InlineData("preparation", "intent-directory")]
        [InlineData("rollback", "foreign")]
        [InlineData("rollback", "modified")]
        [InlineData("rollback", "root")]
        [InlineData("rollback", "mapping")]
        [InlineData("rollback", "manifest")]
        [InlineData("rollback", "symlink")]
        [InlineData("swap", "modified")]
        public async Task HardKill_NewRecoveryEvidence_RejectsTamperingWithoutMutation(string operation, string damage)
        {
            using var temp = new TempDirectory();
            using var outside = new TempDirectory();
            await Run("setup", temp.Path);
            var rollback = operation == "rollback" || operation == "swap";
            await KillAt(rollback ? "hard-rollback" : "hard-publish", temp.Path,
                operation == "partial" ? "during-stage-write" : operation == "swap" ? "before-rollback-swap" : rollback ? "rollback-previous" : "after-stage");
            var metadata = Path.Combine(temp.Path, ".m3u-editor-for-emby");
            var journal = Path.Combine(metadata, "transition.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(journal));
            var directory = rollback ? Path.Combine(metadata, "rollback-current") : Directory.GetDirectories(metadata, "staging-*").Single();
            if (damage == "foreign") File.WriteAllText(Path.Combine(directory, "foreign.sentinel"), "foreign");
            if (damage == "modified")
            {
                var file = operation == "partial" ? Directory.GetFiles(directory, "*.tmp-*", SearchOption.AllDirectories).Single()
                    : Directory.GetFiles(directory, "*", SearchOption.AllDirectories).First();
                File.WriteAllText(file, "MODIFIED");
            }
            if (damage == "root") json["Root"] = outside.Path;
            if (damage == "mapping") json["MappingUuid"] = Guid.NewGuid().ToString();
            if (damage == "manifest") json["Next"]["Files"][0]["Sha256"] = new string('0', 64);
            if (damage == "symlink")
            {
                File.WriteAllText(Path.Combine(outside.Path, "sentinel"), "outside");
                Directory.CreateSymbolicLink(Path.Combine(directory, "link"), outside.Path);
            }
            if (damage.StartsWith("intent-", StringComparison.Ordinal))
            {
                var intent = Directory.GetFiles(metadata, "preparing-file-*.json").Single();
                if (damage == "intent-directory")
                {
                    File.Delete(intent);
                    Directory.CreateDirectory(intent);
                }
                else if (damage == "intent-symlink")
                {
                    var target = Path.Combine(outside.Path, "intent.json");
                    File.Copy(intent, target);
                    File.Delete(intent);
                    File.CreateSymbolicLink(intent, target);
                }
                else
                {
                    var preparing = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(intent));
                    if (damage == "intent-content") preparing["Content"] = "MODIFIED";
                    if (damage == "intent-path") preparing["RelativePath"] = "../foreign.sentinel";
                    File.WriteAllText(intent, preparing.ToJsonString());
                }
            }
            File.WriteAllText(journal, json.ToJsonString());
            var before = Snapshot(temp.Path);
            var directories = Directory.GetDirectories(temp.Path, "*", SearchOption.AllDirectories).OrderBy(path => path).ToArray();
            var outsideBefore = Snapshot(outside.Path);
            for (var retry = 0; retry < 2; retry++)
            {
                await Run("reject-duplicate", temp.Path);
                await Run("reject-rollback", temp.Path);
                Assert.Equal(before, Snapshot(temp.Path));
                Assert.Equal(directories, Directory.GetDirectories(temp.Path, "*", SearchOption.AllDirectories).OrderBy(path => path).ToArray());
                Assert.Equal(outsideBefore, Snapshot(outside.Path));
            }
        }

        [Fact]
        public async Task UnjournaledStagingPrefix_DoesNotConferOwnership()
        {
            using var temp = new TempDirectory();
            var directory = Path.Combine(temp.Path, ".m3u-editor-for-emby", "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "foreign.sentinel"), "untouched");
            var before = Snapshot(temp.Path);
            await Run("reject", temp.Path);
            await Run("reject", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
        }

        [Fact]
        public async Task HardKill_IdenticalContent_MissingPreviousCopyDoesNotAuthorizeMovingActive()
        {
            using var temp = new TempDirectory();
            await Run("setup-identical", temp.Path);
            await KillAt("hard-rollback", temp.Path, "rollback-active");
            var metadata = Path.Combine(temp.Path, ".m3u-editor-for-emby");
            var saved = Path.Combine(metadata, "previous-files");
            var missing = Directory.GetFiles(saved, "*", SearchOption.AllDirectories)
                .First(path => File.Exists(Path.Combine(temp.Path, Path.GetRelativePath(saved, path))));
            File.Delete(missing);
            var before = Snapshot(temp.Path);
            await Run("reject-duplicate", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
            await Run("reject-rollback", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
        }

        [Theory]
        [InlineData("setup-empty-active", "before-rollback-swap")]
        [InlineData("setup-empty-active", "after-rollback-directory-swap")]
        [InlineData("setup-empty-active", "after-rollback-active-manifest")]
        [InlineData("setup-empty-previous", "before-rollback-swap")]
        [InlineData("setup-empty-previous", "after-rollback-directory-swap")]
        [InlineData("setup-empty-previous", "after-rollback-active-manifest")]
        public async Task HardKill_RollbackEmptyGenerations_RemainValid(string setup, string phase)
        {
            using var temp = new TempDirectory();
            await Run(setup, temp.Path);
            await KillAt("hard-rollback", temp.Path, phase);
            await Run("rollback", temp.Path);
            ManagedCrashProcess.Verify(temp.Path);
            await Run("publish", temp.Path);
            await Run("rollback", temp.Path);
            ManagedCrashProcess.Verify(temp.Path);
        }

        [Theory]
        [InlineData("after-journal")]
        [InlineData("after-quarantine")]
        [InlineData("after-manifest")]
        public async Task LegacyV1PublicationJournal_RemainsRecoverable(string phase)
        {
            using var temp = new TempDirectory();
            await Run("setup", temp.Path);
            await KillAt("hard-publish", temp.Path, phase);
            var journal = Path.Combine(temp.Path, ".m3u-editor-for-emby/transition.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(journal)).AsObject();
            var legacyKeys = new[] { "FormatVersion", "Root", "MappingUuid", "Id", "StagingDirectory", "Phase", "Active", "Previous", "Next" };
            Assert.Equal(legacyKeys.OrderBy(key => key), json.Select(pair => pair.Key).OrderBy(key => key));
            Assert.Equal(1, json["FormatVersion"].GetValue<int>());
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(journal), "preparing-file-*"));
            await Run("publish", temp.Path);
            ManagedCrashProcess.Verify(temp.Path);
            var before = Snapshot(temp.Path);
            await Run("publish", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
            await Run("rollback", temp.Path);
        }

        [Fact]
        public async Task HardKill_PreparationCleanup_CanItselfRestart()
        {
            using var temp = new TempDirectory();
            await Run("setup", temp.Path);
            var before = Snapshot(temp.Path);
            await KillAt("hard-publish", temp.Path, "after-stage");
            await KillAt("hard-recover", temp.Path, "recovery-cleanup");
            await Run("duplicate", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
            await Run("duplicate", temp.Path);
            Assert.Equal(before, Snapshot(temp.Path));
        }

        private static async Task KillAt(string mode, string root, string phase)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { typeof(ManagedCrashProcess).Assembly.Location, mode, root, phase }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
            string line;
            try
            {
                line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.True(line != null && line.StartsWith("CHECKPOINT "), $"Checkpoint not reached: {line}; {(process.HasExited ? await error : "still running")}");
                using var checkpoint = System.Text.Json.JsonDocument.Parse(line.Substring(11));
                Assert.Equal(phase, checkpoint.RootElement.GetProperty("Phase").GetString());
                if (phase == "rollback-active" || phase == "rollback-previous")
                    Assert.Equal(2, checkpoint.RootElement.GetProperty("MoveCount").GetInt32());
                if (phase == "during-stage-write")
                {
                    var partial = Directory.GetFiles(root, "*.tmp-*", SearchOption.AllDirectories).Single();
                    Assert.Equal(65536, new FileInfo(partial).Length);
                    var intent = Directory.GetFiles(Path.Combine(root, ".m3u-editor-for-emby"), "preparing-file-*.json").Single();
                    using var preparing = System.Text.Json.JsonDocument.Parse(File.ReadAllText(intent));
                    Assert.True(System.Text.Encoding.UTF8.GetByteCount(preparing.RootElement.GetProperty("Content").GetString()) > new FileInfo(partial).Length);
                }
                process.Kill(true); // SIGKILL on Linux, not exception compensation or Environment.Exit.
                await process.WaitForExitAsync(timeout.Token);
                if (OperatingSystem.IsLinux()) Assert.Equal(137, process.ExitCode);
                else Assert.NotEqual(0, process.ExitCode);
                RecordEvidence(new { Source = Environment.GetEnvironmentVariable("MANAGED_CRASH_SOURCE"), Root = root,
                    Mode = mode, Phase = phase, Killed = true, ExitCode = process.ExitCode,
                    Checkpoint = checkpoint.RootElement.Clone(), AfterKill = Snapshot(root) });
            }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        }

        private static void RecordEvidence(object value)
        {
            var directory = Environment.GetEnvironmentVariable("MANAGED_CRASH_EVIDENCE_DIR");
            if (string.IsNullOrEmpty(directory)) return;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), System.Text.Json.JsonSerializer.Serialize(value));
        }

        private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path).Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();

        private static async Task Run(string mode, string root, string phase = "none", int expectedExit = 0)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(typeof(ManagedCrashProcess).Assembly.Location);
            start.ArgumentList.Add(mode);
            start.ArgumentList.Add(root);
            start.ArgumentList.Add(phase);
            using var process = Process.Start(start);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(true); throw; }
            var actualOutput = await output;
            var actualError = await error;
            RecordEvidence(new { Source = Environment.GetEnvironmentVariable("MANAGED_CRASH_SOURCE"), Mode = mode, Phase = phase,
                Root = root, ExitCode = process.ExitCode, ExpectedExit = expectedExit, Output = actualOutput, Error = actualError,
                AfterRestart = Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path)
                    .Select(path => new { Path = Path.GetRelativePath(root, path), Length = new FileInfo(path).Length,
                        Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) }).ToArray() });
            Assert.True(process.ExitCode == expectedExit, $"{mode}/{phase}: exit {process.ExitCode}, expected {expectedExit}\n{actualOutput}\n{actualError}");
        }
    }
}
