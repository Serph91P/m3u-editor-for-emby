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
            Assert.True(process.ExitCode == expectedExit, $"{mode}/{phase}: exit {process.ExitCode}, expected {expectedExit}\n{await output}\n{await error}");
        }
    }
}
