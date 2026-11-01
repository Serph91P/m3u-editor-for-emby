using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client.Models;
using Emby.M3uEditor.Plugin.Service;
using Emby.M3uEditor.Plugin.Tests.Fakes;

namespace Emby.M3uEditor.Plugin.Tests
{
    // This entry point deliberately bypasses catch/finally in the publisher.
    internal static class ManagedCrashProcess
    {
        internal const string MappingUuid = "123e4567-e89b-12d3-a456-426614174000";
        public static async Task<int> Main(string[] args)
        {
            var root = args[1];
            using var handler = new FakeHttpHandler();
            using var http = new HttpClient(handler);
            var service = new StrmSyncService(null, http);
            var config = new PluginConfiguration
            {
                BaseUrl = "https://editor.example", Username = "synthetic", Password = "synthetic",
                ManagedApprovedOutputRoots = root, ManagedPublishingIntegrationId = 7, ManagedSetupReady = true, ManagedSetupLastResult = "Ready"
            };
            service.ManagedConfigurationProvider = () => config;
            if (args[0] == "reconcile")
            {
                handler.RespondWith("player_api.php?username=", "{\"m3u_editor\":{\"library_publishing\":{\"api_version\":1,\"actions\":{\"register_publisher\":\"m3u_editor_register_publisher\",\"catalog\":\"m3u_editor_catalog\",\"sync_result\":\"m3u_editor_sync_result\"},\"snapshot_mode\":\"full\",\"features\":[\"library_mappings\",\"variants\",\"provider_failover\",\"local_nfo\",\"revision_metadata\"]}}}");
                handler.RespondWith("action=m3u_editor_register_publisher", "{}");
                handler.RespondWith("action=m3u_editor_catalog", JsonSerializer.Serialize(new M3uEditorCatalog
                {
                    ApiVersion = 1, FullSnapshot = true, Revision = new string('c', 64), Mappings = new List<M3uEditorMapping> { Mapping(root, 'c') }
                }));
                handler.RespondWith("action=m3u_editor_sync_result", JsonSerializer.Serialize(new M3uEditorResponse<M3uEditorSyncResult>
                {
                    ApiVersion = 1, Data = new M3uEditorSyncResult { Applied = true, MappingUuid = MappingUuid, Revision = new string('c', 64) }
                }));
                var result = await service.ReconcileManagedAsync(config, () => { }, null, CancellationToken.None, () => { });
                Require(result.Success && result.AppliedMappings == 1, result.Error);
                Verify(root);
                return 0;
            }
            if (args[0] == "setup")
            {
                Directory.CreateDirectory(root);
                Require((await service.PublishManagedMappingAsync(Mapping(root, 'a'), CancellationToken.None)).Success);
                Require((await service.PublishManagedMappingAsync(Mapping(root, 'b'), CancellationToken.None)).Success);
                File.WriteAllText(Path.Combine(root, "foreign.sentinel"), "untouched");
                return 0;
            }
            if (args[0] == "crash" || args[0] == "case-crash" || args[0] == "recover-crash")
            {
                var count = 0;
                service.ManagedPhaseHook = phase => { if (phase == args[2]) Environment.Exit(73); };
                service.ManagedFileMoveHook = (operation, path) =>
                {
                    if (operation == args[2] && ++count == 2) Environment.Exit(73);
                };
            }
            if (args[0] == "rollback" || args[0] == "reject-rollback")
            {
                var result = await service.RollbackManagedMappingAsync(root, MappingUuid, CancellationToken.None, null, root);
                if (args[0] == "reject-rollback") { Require(!result.Success); return 0; }
                Require(result.Success, result.Error);
                Verify(root);
                return 0;
            }
            var revision = args[0] == "duplicate" || args[0] == "recover-crash" || args[0] == "reject-duplicate" ? 'b' : 'c';
            var mapping = Mapping(root, revision);
            if (args[0] == "case-crash") mapping.Items[0].RelativeFolder = "synthetic";
            var publish = await service.PublishManagedMappingAsync(mapping, CancellationToken.None);
            if (args[0] == "reject" || args[0] == "reject-duplicate") { Require(!publish.Success); return 0; }
            Require(publish.Success, publish.Error);
            if (args[0] == "crash" || args[0] == "case-crash" || args[0] == "recover-crash") throw new Exception("Crash hook not reached");
            Verify(root);
            Require(File.ReadAllText(Path.Combine(root, "foreign.sentinel")) == "untouched");
            return 0;
        }

        internal static M3uEditorMapping Mapping(string root, char revision)
        {
            var mapping = new M3uEditorMapping
        {
            MappingUuid = MappingUuid, IntegrationId = 7, Revision = new string(revision, 64), FullSnapshot = true,
            TargetLibrary = new M3uEditorTargetLibrary { Name = "Synthetic", CollectionType = "movies", OutputPath = root, Managed = true },
            Options = new M3uEditorMappingOptions { Nfo = true, Versions = true, Cleanup = "replace" },
            Items = new List<M3uEditorCatalogItem> { new M3uEditorCatalogItem
            {
                CanonicalId = "movie:synthetic:1", MediaType = "movie", DisplayTitle = "Synthetic", RelativeFolder = "Synthetic", BaseFilename = "Synthetic",
                Ids = new M3uEditorProviderIds(),
                Nfo = new M3uEditorNfo { Title = "Synthetic", Plot = "Generation " + revision, Genres = JsonSerializer.SerializeToElement(new string[0]), Ids = new M3uEditorProviderIds() },
                Variants = new List<M3uEditorVariant> { new M3uEditorVariant { Key = "default", TechnicalMetadata = JsonSerializer.SerializeToElement((object)null),
                    Preferred = new M3uEditorSource { SourceId = 1, PlaybackUrl = "https://editor.example/" + revision } } }
            } }
        };
            mapping.Items[0].Variants.Add(new M3uEditorVariant
            {
                Key = revision == 'c' ? "new" : "old", TechnicalMetadata = JsonSerializer.SerializeToElement((object)null),
                Preferred = new M3uEditorSource { SourceId = 2, PlaybackUrl = "https://editor.example/extra/" + revision }
            });
            return mapping;
        }

        internal static void Verify(string root)
        {
            var metadata = Path.Combine(root, ".m3u-editor-for-emby");
            foreach (var name in new[] { "active", "previous" })
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(metadata, name + ".json")));
                var filesRoot = name == "active" ? root : Path.Combine(metadata, "previous-files");
                var expected = manifest.RootElement.GetProperty("Files").EnumerateArray()
                    .Select(file => file.GetProperty("RelativePath").GetString()).OrderBy(path => path).ToArray();
                var actual = Directory.GetFiles(filesRoot, "*", SearchOption.AllDirectories)
                    .Where(path => !path.StartsWith(metadata + Path.DirectorySeparatorChar) || name != "active")
                    .Where(path => Path.GetFileName(path) != "foreign.sentinel")
                    .Select(path => Path.GetRelativePath(filesRoot, path).Replace(Path.DirectorySeparatorChar, '/')).OrderBy(path => path).ToArray();
                Require(expected.SequenceEqual(actual), "Unexpected or missing generation files");
                foreach (var file in manifest.RootElement.GetProperty("Files").EnumerateArray())
                {
                    var bytes = File.ReadAllBytes(Path.Combine(filesRoot, file.GetProperty("RelativePath").GetString()));
                    Require(bytes.LongLength == file.GetProperty("Length").GetInt64());
                    Require(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() == file.GetProperty("Sha256").GetString());
                }
            }
        }

        private static void Require(bool value, string message = "Generation effect mismatch")
        {
            if (!value) throw new InvalidOperationException(message);
        }
    }
}
