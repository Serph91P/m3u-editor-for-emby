using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class ManagedPublishingTests : SyncTestBase
    {
        [Fact]
        public async Task PublishManagedMappingAsync_MovieVariants_WritesEightVersionsOneNfoAndManifest()
        {
            var mapping = MovieMapping(10);

            var result = await MakeService().PublishManagedMappingAsync(mapping, None);

            Assert.True(result.Success, result.Error);
            Assert.Equal(2, result.OmittedVersions);
            var strmFiles = Directory.GetFiles(TempDir.Path, "*.strm", SearchOption.AllDirectories);
            Assert.Equal(8, strmFiles.Length);
            Assert.Contains(strmFiles, path => path.EndsWith("Movie - v00.strm"));
            Assert.Contains(strmFiles, path => path.EndsWith("Movie - v07.strm"));
            Assert.DoesNotContain(strmFiles, path => path.EndsWith("Movie - v08.strm"));
            Assert.DoesNotContain(Directory.GetFiles(TempDir.Path, "*.strm", SearchOption.AllDirectories),
                path => File.ReadAllText(path).Contains("/backup/"));
            Assert.Single(Directory.GetFiles(TempDir.Path, "*.nfo", SearchOption.AllDirectories));
            Assert.True(File.Exists(Path.Combine(TempDir.Path, ".m3u-editor-for-emby", "active.json")));
        }

        [Theory]
        [InlineData("after-stage")]
        [InlineData("after-quarantine")]
        [InlineData("after-publish")]
        [InlineData("before-manifest")]
        public async Task PublishManagedMappingAsync_InjectedPhaseFailure_RestoresPreviousGeneration(string phase)
        {
            var service = MakeService();
            var original = MovieMapping(1);
            var first = await service.PublishManagedMappingAsync(original, None);
            Assert.True(first.Success, first.Error);

            service.ManagedPhaseHook = currentPhase =>
            {
                if (currentPhase == phase)
                {
                    throw new IOException("Injected publication failure.");
                }
            };

            var replacement = MovieMapping(
                1,
                "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                "https://editor.example/replacement/");
            var failed = await service.PublishManagedMappingAsync(replacement, None);

            Assert.False(failed.Success);
            var activeStrm = Assert.Single(Directory.GetFiles(TempDir.Path, "*.strm", SearchOption.AllDirectories)
                .Where(path => !path.Contains(Path.DirectorySeparatorChar + ".m3u-editor-for-emby" + Path.DirectorySeparatorChar)));
            Assert.Contains("https://editor.example/play/0", File.ReadAllText(activeStrm));
            var activeManifest = File.ReadAllText(Path.Combine(TempDir.Path, ".m3u-editor-for-emby", "active.json"));
            Assert.Contains(original.Revision, activeManifest);
            Assert.DoesNotContain(replacement.Revision, activeManifest);
        }

        [Fact]
        public async Task RollbackManagedMappingAsync_PreviousGeneration_RestoresItAtomically()
        {
            var service = MakeService();
            var original = MovieMapping(1);
            var replacement = MovieMapping(
                1,
                "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                "https://editor.example/replacement/");
            Assert.True((await service.PublishManagedMappingAsync(original, None)).Success);
            Assert.True((await service.PublishManagedMappingAsync(replacement, None)).Success);

            var rollback = await service.RollbackManagedMappingAsync(
                TempDir.Path,
                original.MappingUuid,
                None);

            Assert.True(rollback.Success, rollback.Error);
            Assert.Equal(original.Revision, rollback.Revision);
            Assert.Equal(replacement.Revision, rollback.PreviousRevision);
            var activeStrm = Assert.Single(Directory.GetFiles(TempDir.Path, "*.strm", SearchOption.AllDirectories)
                .Where(path => !path.Contains(Path.DirectorySeparatorChar + ".m3u-editor-for-emby" + Path.DirectorySeparatorChar)));
            Assert.Contains("https://editor.example/play/0", File.ReadAllText(activeStrm));
        }

        [Fact]
        public async Task PublishManagedMappingAsync_UnownedFile_PreservesIt()
        {
            var foreignPath = Path.Combine(TempDir.Path, "keep.txt");
            File.WriteAllText(foreignPath, "foreign");

            var result = await MakeService().PublishManagedMappingAsync(MovieMapping(1), None);

            Assert.True(result.Success, result.Error);
            Assert.Equal("foreign", File.ReadAllText(foreignPath));
        }

        [Fact]
        public async Task PublishManagedMappingAsync_PreexistingPluginMetadata_FailsWithoutAdoptingForeignFiles()
        {
            var metadataRoot = Path.Combine(TempDir.Path, ".m3u-editor-for-emby");
            Directory.CreateDirectory(metadataRoot);
            var foreignPath = Path.Combine(metadataRoot, "foreign.txt");
            File.WriteAllText(foreignPath, "foreign");

            var result = await MakeService().PublishManagedMappingAsync(MovieMapping(1), None);

            Assert.False(result.Success);
            Assert.Equal("foreign", File.ReadAllText(foreignPath));
            Assert.False(File.Exists(Path.Combine(metadataRoot, "active.json")));
        }

        [Fact]
        public async Task PublishManagedMappingAsync_SymlinkedCatalogFolder_RejectsEscape()
        {
            using (var outside = new Fakes.TempDirectory())
            {
                Directory.CreateSymbolicLink(Path.Combine(TempDir.Path, "linked"), outside.Path);
                var mapping = MovieMapping(1);
                mapping.Items[0].RelativeFolder = "linked";

                var result = await MakeService().PublishManagedMappingAsync(mapping, None);

                Assert.False(result.Success);
                Assert.Empty(Directory.GetFiles(outside.Path));
            }
        }

        [Fact]
        public async Task PublishManagedMappingAsync_CleanupKeep_RetainsStaleOwnedFiles()
        {
            var service = MakeService();
            Assert.True((await service.PublishManagedMappingAsync(MovieMapping(2), None)).Success);
            var replacement = MovieMapping(
                1,
                "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                "https://editor.example/replacement/");
            replacement.Options.Cleanup = "keep";

            var result = await service.PublishManagedMappingAsync(replacement, None);

            Assert.True(result.Success, result.Error);
            Assert.Equal(2, Directory.GetFiles(TempDir.Path, "*.strm", SearchOption.AllDirectories)
                .Count(path => !path.Contains(Path.DirectorySeparatorChar + ".m3u-editor-for-emby" + Path.DirectorySeparatorChar)));
            var activeManifest = File.ReadAllText(Path.Combine(TempDir.Path, ".m3u-editor-for-emby", "active.json"));
            Assert.Contains("Movie - v01.strm", activeManifest);
        }

        [Fact]
        public async Task ReconcileManagedAsync_CompatibleCatalog_PublishesAndReportsExactRevision()
        {
            var mapping = MovieMapping(1);
            var catalogRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            Handler.RespondWith("player_api.php?username=", CapabilityJson);
            Handler.RespondWith("action=m3u_editor_catalog", JsonSerializer.Serialize(new M3uEditorCatalog
            {
                ApiVersion = 1,
                FullSnapshot = true,
                Revision = catalogRevision,
                Mappings = new List<M3uEditorMapping> { mapping }
            }));
            Handler.RespondWith("action=m3u_editor_sync_result", JsonSerializer.Serialize(new M3uEditorResponse<M3uEditorSyncResult>
            {
                ApiVersion = 1,
                Data = new M3uEditorSyncResult
                {
                    Applied = true,
                    Duplicate = false,
                    MappingUuid = mapping.MappingUuid,
                    Revision = mapping.Revision
                }
            }));
            var config = DefaultConfig();

            var result = await MakeService().ReconcileManagedAsync(config, SaveConfig, null, None);

            Assert.True(result.Compatible);
            Assert.True(result.Success, result.Error);
            Assert.Equal(1, result.AppliedMappings);
            Assert.Equal(catalogRevision, config.ManagedCatalogRevision);
            Assert.Equal(mapping.Revision, config.ManagedActiveGeneration);
            Assert.True(config.ManagedPublishingEnabled);
            Assert.Contains("2 added", config.ManagedDryRunSummary);
            Assert.Equal(1, Handler.ReceivedBodies.Count(body => body.Contains("status=success")));
            Assert.Contains(Handler.ReceivedBodies, body => body.Contains("revision=" + mapping.Revision));
        }

        [Fact]
        public async Task ReconcileManagedAsync_NoCompatibleCapability_LeavesGenericXtreamModeUntouched()
        {
            Handler.RespondWith("player_api.php?username=", "{\"user_info\":{\"auth\":1}}");
            var config = DefaultConfig();

            var result = await MakeService().ReconcileManagedAsync(config, SaveConfig, null, None);

            Assert.False(result.Compatible);
            Assert.True(result.Success);
            Assert.False(config.ManagedPublishingEnabled);
            Assert.Single(Handler.ReceivedUrls);
        }

        [Fact]
        public async Task ReconcileManagedAsync_CallbackFailure_RemovesFirstGenerationAndDoesNotReportSuccess()
        {
            var mapping = MovieMapping(1);
            Handler.RespondWith("player_api.php?username=", CapabilityJson);
            Handler.RespondWith("action=m3u_editor_catalog", JsonSerializer.Serialize(new M3uEditorCatalog
            {
                ApiVersion = 1,
                FullSnapshot = true,
                Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Mappings = new List<M3uEditorMapping> { mapping }
            }));
            Handler.RespondWith("action=m3u_editor_sync_result", "{}", HttpStatusCode.ServiceUnavailable);

            var result = await MakeService().ReconcileManagedAsync(DefaultConfig(), SaveConfig, null, None);

            Assert.False(result.Success);
            Assert.Empty(Directory.GetFiles(TempDir.Path, "*.strm", SearchOption.AllDirectories));
            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, ".m3u-editor-for-emby")));
            Assert.DoesNotContain(Handler.ReceivedBodies, body => body.Contains("status=failed"));
        }

        [Fact]
        public async Task ReconcileManagedAsync_EnabledLegacyWriterOnSameRoot_FailsBeforeWriting()
        {
            var mapping = MovieMapping(1);
            Handler.RespondWith("player_api.php?username=", CapabilityJson);
            Handler.RespondWith("action=m3u_editor_catalog", JsonSerializer.Serialize(new M3uEditorCatalog
            {
                ApiVersion = 1,
                FullSnapshot = true,
                Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Mappings = new List<M3uEditorMapping> { mapping }
            }));
            Handler.RespondWith("action=m3u_editor_sync_result", "{\"error\":\"sync_failed\"}", HttpStatusCode.UnprocessableEntity);
            var config = DefaultConfig();
            config.SyncMovies = true;

            var result = await MakeService().ReconcileManagedAsync(config, SaveConfig, null, None);

            Assert.False(result.Success);
            Assert.Empty(Directory.GetFiles(TempDir.Path, "*.strm", SearchOption.AllDirectories));
            Assert.Contains(Handler.ReceivedBodies, body => body.Contains("status=failed"));
        }

        [Fact]
        public void HasManagedOwnershipConflict_OverlappingPersistedMapping_BlocksLegacyWriter()
        {
            var config = DefaultConfig();
            config.ManagedMappingsJson = JsonSerializer.Serialize(new List<ManagedMappingState>
            {
                new ManagedMappingState
                {
                    MappingUuid = "123e4567-e89b-12d3-a456-426614174000",
                    CollectionType = "movies",
                    OutputPath = Path.Combine(TempDir.Path, "Movies")
                }
            });

            Assert.True(StrmSyncService.HasManagedOwnershipConflict(config, "movies"));
            Assert.False(StrmSyncService.HasManagedOwnershipConflict(config, "tvshows"));
        }

        [Fact]
        public async Task PublishManagedMappingAsync_OverlappingRunOnSameRoot_IsExcluded()
        {
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var firstService = MakeService();
            firstService.ManagedPhaseHook = phase =>
            {
                if (phase == "after-stage")
                {
                    entered.Set();
                    release.Wait();
                }
            };

            var firstTask = Task.Run(() => firstService.PublishManagedMappingAsync(MovieMapping(1), None));
            Assert.True(entered.Wait(5000));

            var overlapping = await MakeService().PublishManagedMappingAsync(MovieMapping(1), None);
            release.Set();
            var first = await firstTask;

            Assert.False(overlapping.Success);
            Assert.Contains("already running", overlapping.Error);
            Assert.True(first.Success, first.Error);
        }

        [Fact]
        public async Task PublishManagedMappingAsync_SeriesEpisodeVersions_WriteCanonicalNfos()
        {
            var mapping = SeriesMapping();

            var result = await MakeService().PublishManagedMappingAsync(mapping, None);

            Assert.True(result.Success, result.Error);
            Assert.Single(Directory.GetFiles(TempDir.Path, "tvshow.nfo", SearchOption.AllDirectories));
            Assert.Single(Directory.GetFiles(TempDir.Path, "*.nfo", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith("tvshow.nfo")));
            var episodes = Directory.GetFiles(TempDir.Path, "*.strm", SearchOption.AllDirectories);
            Assert.Equal(2, episodes.Length);
            Assert.All(episodes, path => Assert.Contains("s01e02", Path.GetFileName(path)));
        }

        private const string CapabilityJson = @"{
  ""m3u_editor"": {
    ""library_publishing"": {
      ""api_version"": 1,
      ""actions"": { ""catalog"": ""m3u_editor_catalog"", ""sync_result"": ""m3u_editor_sync_result"" },
      ""snapshot_mode"": ""full"",
      ""features"": [""library_mappings"", ""variants"", ""provider_failover"", ""local_nfo"", ""revision_metadata""]
    }
  }
}";

        private M3uEditorMapping MovieMapping(
            int variantCount,
            string revision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            string playbackBase = "https://editor.example/play/")
        {
            var variants = new List<M3uEditorVariant>();
            for (var index = 0; index < variantCount; index++)
            {
                variants.Add(new M3uEditorVariant
                {
                    Key = "v" + index.ToString("00"),
                    Preferred = new M3uEditorSource
                    {
                        SourceId = index + 1,
                        PlaybackUrl = playbackBase + index
                    },
                    Failover = new List<M3uEditorSource>
                    {
                        new M3uEditorSource
                        {
                            SourceId = index + 101,
                            PlaybackUrl = "https://editor.example/backup/" + index
                        }
                    },
                    TechnicalMetadata = EmptyJsonArray()
                });
            }

            return new M3uEditorMapping
            {
                MappingUuid = "123e4567-e89b-12d3-a456-426614174000",
                IntegrationId = 7,
                Revision = revision,
                FullSnapshot = true,
                TargetLibrary = new M3uEditorTargetLibrary
                {
                    Id = "library-1",
                    Name = "Managed Movies",
                    CollectionType = "movies",
                    OutputPath = TempDir.Path,
                    Managed = true
                },
                Options = new M3uEditorMappingOptions
                {
                    Naming = "media-year",
                    Nfo = true,
                    Versions = true,
                    Cleanup = "replace",
                    Refresh = true
                },
                Items = new List<M3uEditorCatalogItem>
                {
                    new M3uEditorCatalogItem
                    {
                        CanonicalId = "movie:tmdb:603",
                        MediaType = "movie",
                        DisplayTitle = "Movie",
                        OriginalTitle = "Original Movie",
                        Year = 1999,
                        RelativeFolder = "Movie",
                        BaseFilename = "Movie",
                        Ids = new M3uEditorProviderIds { Tmdb = 603, Imdb = "tt0133093" },
                        Nfo = new M3uEditorNfo
                        {
                            Title = "Movie",
                            OriginalTitle = "Original Movie",
                            Year = 1999,
                            Plot = "Plot",
                            Genres = EmptyJsonArray(),
                            Ids = new M3uEditorProviderIds { Tmdb = 603, Imdb = "tt0133093" }
                        },
                        Variants = variants
                    }
                }
            };
        }

        private M3uEditorMapping SeriesMapping()
        {
            var ids = new M3uEditorProviderIds();
            var episode = new M3uEditorCatalogItem
            {
                CanonicalId = "episode:series:title:show:s01e02",
                SeriesCanonicalId = "series:title:show",
                MediaType = "episode",
                DisplayTitle = "Episode",
                OriginalTitle = "Episode",
                SeasonNumber = 1,
                EpisodeNumber = 2,
                RelativeFolder = "season-01",
                BaseFilename = "show-s01e02-episode",
                Ids = ids,
                Nfo = new M3uEditorNfo
                {
                    Title = "Episode",
                    OriginalTitle = "Episode",
                    SeasonNumber = 1,
                    EpisodeNumber = 2,
                    Genres = EmptyJsonArray(),
                    Ids = ids
                },
                Variants = new List<M3uEditorVariant>
                {
                    new M3uEditorVariant
                    {
                        Key = "1080p",
                        Preferred = new M3uEditorSource { SourceId = 1, PlaybackUrl = "https://editor.example/play/episode-hd" },
                        TechnicalMetadata = EmptyJsonArray()
                    },
                    new M3uEditorVariant
                    {
                        Key = "2160p",
                        Preferred = new M3uEditorSource { SourceId = 2, PlaybackUrl = "https://editor.example/play/episode-uhd" },
                        TechnicalMetadata = EmptyJsonArray()
                    }
                }
            };
            return new M3uEditorMapping
            {
                MappingUuid = "123e4567-e89b-12d3-a456-426614174001",
                IntegrationId = 7,
                Revision = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
                FullSnapshot = true,
                TargetLibrary = new M3uEditorTargetLibrary
                {
                    Name = "Managed Shows",
                    CollectionType = "tvshows",
                    OutputPath = TempDir.Path,
                    Managed = true
                },
                Options = new M3uEditorMappingOptions { Nfo = true, Versions = true, Cleanup = "replace", Refresh = true },
                Items = new List<M3uEditorCatalogItem>
                {
                    new M3uEditorCatalogItem
                    {
                        CanonicalId = "series:title:show",
                        MediaType = "series",
                        DisplayTitle = "Show",
                        OriginalTitle = "Show",
                        RelativeFolder = "show",
                        BaseFilename = "show",
                        Ids = ids,
                        Nfo = new M3uEditorNfo { Title = "Show", OriginalTitle = "Show", Genres = EmptyJsonArray(), Ids = ids },
                        Episodes = new List<M3uEditorCatalogItem> { episode }
                    }
                }
            };
        }

        private static JsonElement EmptyJsonArray()
        {
            using (var document = JsonDocument.Parse("[]"))
            {
                return document.RootElement.Clone();
            }
        }
    }
}
