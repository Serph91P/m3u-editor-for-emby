using System.Collections.Generic;
using System.Net.Http;
using System;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client;
using Emby.Xtream.Plugin.Tests.Fakes;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class M3uEditorClientTests
    {
        [Fact]
        public async Task GetCatalogAsync_ExactVersionOnePayload_DeserializesVariantsAndFailover()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("action=m3u_editor_catalog", CatalogJson);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var catalog = await client.GetCatalogAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None);

                Assert.Equal(1, catalog.ApiVersion);
                Assert.True(catalog.FullSnapshot);
                Assert.Equal(Revision, catalog.Revision);
                var mapping = Assert.Single(catalog.Mappings);
                Assert.Equal("movies", mapping.TargetLibrary.CollectionType);
                var movie = Assert.Single(mapping.Items);
                Assert.Equal("movie", movie.MediaType);
                var variant = Assert.Single(movie.Variants);
                Assert.Equal("1080p-sdr-h264-aac-en-theatrical", variant.Key);
                Assert.Equal("https://editor.example/play/primary", variant.Preferred.PlaybackUrl);
                Assert.Equal("https://editor.example/play/backup", Assert.Single(variant.Failover).PlaybackUrl);
            }
        }

        [Theory]
        [MemberData(nameof(InvalidCatalogs))]
        public async Task GetCatalogAsync_InvalidContract_FailsClosedWithoutSecrets(string json, string expectedMessage)
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("action=m3u_editor_catalog", json);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var error = await Assert.ThrowsAsync<System.InvalidOperationException>(() => client.GetCatalogAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None));

                Assert.Contains(expectedMessage, error.Message);
                Assert.DoesNotContain("account", error.ToString());
                Assert.DoesNotContain("credential", error.ToString());
                Assert.DoesNotContain("https://", error.ToString());
            }
        }

        [Fact]
        public async Task DiscoverCapabilityAsync_CompatibleAdvertisement_ReturnsVersionOneCapability()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("player_api.php?username=", CapabilityJson);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var capability = await client.DiscoverCapabilityAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None);

                Assert.NotNull(capability);
                Assert.Equal(1, capability.ApiVersion);
            }
        }

        [Fact]
        public async Task ReportSyncResultAsync_ExactRevision_PostsRedactedResult()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("action=m3u_editor_sync_result", "{\"api_version\":1,\"data\":{\"applied\":true,\"duplicate\":false,\"mapping_uuid\":\"123e4567-e89b-12d3-a456-426614174000\",\"revision\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}}");
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var result = await client.ReportSyncResultAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    7,
                    "123e4567-e89b-12d3-a456-426614174000",
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    true,
                    "Published from https://provider.example/item using credential",
                    null,
                    CancellationToken.None);

                Assert.True(result.Applied);
                var body = Assert.Single(handler.ReceivedBodies);
                Assert.Contains("status=success", body);
                Assert.Contains("revision=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", body);
                Assert.DoesNotContain("provider.example", body);
                Assert.DoesNotContain("credential", body);
            }
        }

        [Fact]
        public async Task GetCatalogAsync_TransientServerFailure_RetriesIdempotentReadOnce()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("action=m3u_editor_catalog", "{}", System.Net.HttpStatusCode.ServiceUnavailable);
            handler.RespondWith("action=m3u_editor_catalog", CatalogJson);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var catalog = await client.GetCatalogAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None);

                Assert.Equal(Revision, catalog.Revision);
                Assert.Equal(2, handler.ReceivedUrls.Count);
            }
        }

        [Fact]
        public async Task DiscoverCapabilityAsync_TransientServerFailure_RetriesIdempotentReadOnce()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("player_api.php?username=", "{}", System.Net.HttpStatusCode.ServiceUnavailable);
            handler.RespondWith("player_api.php?username=", CapabilityJson);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var capability = await client.DiscoverCapabilityAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None);

                Assert.NotNull(capability);
                Assert.Equal(2, handler.ReceivedUrls.Count);
            }
        }

        [Fact]
        public async Task GetCatalogAsync_UnsafeBackendUrl_FailsBeforeSendingCredentials()
        {
            var handler = new FakeHttpHandler();
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var error = await Assert.ThrowsAsync<System.InvalidOperationException>(() => client.GetCatalogAsync(
                    "file:///tmp/backend",
                    "account",
                    "credential",
                    CancellationToken.None));

                Assert.Contains("base URL", error.Message);
                Assert.Empty(handler.ReceivedUrls);
            }
        }

        [Fact]
        public async Task GetCatalogAsync_TransportError_DoesNotExposeAuthenticatedUrl()
        {
            using (var httpClient = new HttpClient(new ThrowingHandler()))
            {
                var client = new M3uEditorClient(httpClient);

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCatalogAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None));

                Assert.DoesNotContain("account", error.ToString());
                Assert.DoesNotContain("credential", error.ToString());
                Assert.DoesNotContain("https://", error.ToString());
            }
        }

        public static IEnumerable<object[]> InvalidCatalogs
        {
            get
            {
                yield return new object[] { CatalogJson.Replace("\"api_version\": 1", "\"api_version\": 2"), "API version" };
                yield return new object[] { CatalogJson.Replace(Revision, "invalid"), "catalog revision" };
                yield return new object[] { CatalogJson.Replace("123e4567-e89b-12d3-a456-426614174000", "not-a-uuid"), "mapping identity" };
                yield return new object[] { CatalogJson.Replace("\"collection_type\": \"movies\"", "\"collection_type\": \"music\""), "collection type" };
                yield return new object[] { CatalogJson.Replace("/media/managed-movies", "relative/path"), "output path" };
                yield return new object[] { CatalogJson.Replace("/media/managed-movies", "/"), "output path" };
                yield return new object[] { CatalogJson.Replace("the-matrix-1999", "../escape"), "relative path" };
                yield return new object[] { CatalogJson.Replace("https://editor.example/play/primary", "file:///etc/passwd"), "playback URL" };
                yield return new object[] { CatalogJson.Replace("\"tmdb\": 603", "\"tmdb\": 0"), "provider ID" };
                yield return new object[] { CatalogJson.Replace("A choice.", "bad\\u0001xml"), "NFO" };
            }
        }

        private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private const string CapabilityJson = @"{
  ""user_info"": { ""auth"": 1 },
  ""m3u_editor"": {
    ""library_publishing"": {
      ""api_version"": 1,
      ""actions"": { ""catalog"": ""m3u_editor_catalog"", ""sync_result"": ""m3u_editor_sync_result"" },
      ""snapshot_mode"": ""full"",
      ""features"": [""library_mappings"", ""variants"", ""provider_failover"", ""local_nfo"", ""revision_metadata""]
    }
  }
}";

        private const string CatalogJson = @"{
  ""api_version"": 1,
  ""full_snapshot"": true,
  ""revision"": ""aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"",
  ""mappings"": [{
    ""mapping_uuid"": ""123e4567-e89b-12d3-a456-426614174000"",
    ""integration_id"": 7,
    ""target_library"": {
      ""id"": ""library-1"",
      ""name"": ""Managed Movies"",
      ""collection_type"": ""movies"",
      ""output_path"": ""/media/managed-movies"",
      ""managed"": true
    },
    ""options"": { ""naming"": ""media-year"", ""nfo"": true, ""versions"": true, ""cleanup"": ""replace"", ""refresh"": true },
    ""full_snapshot"": true,
    ""revision"": ""bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"",
    ""items"": [{
      ""canonical_id"": ""movie:tmdb:603"",
      ""media_type"": ""movie"",
      ""display_title"": ""The Matrix"",
      ""display_title_source"": ""channel.title"",
      ""original_title"": ""The Matrix"",
      ""original_title_source"": ""display_title"",
      ""year"": 1999,
      ""ids"": { ""tmdb"": 603, ""tvdb"": null, ""imdb"": ""tt0133093"" },
      ""groups"": [""Science Fiction""],
      ""relative_folder"": ""the-matrix-1999"",
      ""base_filename"": ""the-matrix-1999"",
      ""nfo"": { ""title"": ""The Matrix"", ""original_title"": ""The Matrix"", ""year"": 1999, ""plot"": ""A choice."", ""genres"": [""Science Fiction""], ""ids"": { ""tmdb"": 603, ""tvdb"": null, ""imdb"": ""tt0133093"" } },
      ""variants"": [{
        ""key"": ""1080p-sdr-h264-aac-en-theatrical"",
        ""preferred"": { ""source_id"": 10, ""playback_url"": ""https://editor.example/play/primary"", ""playlist_id"": 2 },
        ""failover"": [{ ""source_id"": 11, ""playback_url"": ""https://editor.example/play/backup"", ""playlist_id"": 3 }],
        ""technical_metadata"": []
      }]
    }]
  }]
}";

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                return Task.FromException<HttpResponseMessage>(new HttpRequestException(
                    "Transport failed at https://sensitive.invalid/ for account credential"));
            }
        }
    }
}
