using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Client.Models;
using Emby.M3uEditor.Plugin.Tests.Fakes;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
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
        public async Task GetCatalogAsync_StaleRevisionConflict_RetriesUntilCatalogIsPublished()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith(
                "action=m3u_editor_catalog",
                "{\"error\":{\"code\":\"stale_revision\"}}",
                System.Net.HttpStatusCode.Conflict);
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
        public async Task GetCatalogAsync_IncompletePublicationConflict_StopsAfterThreeAttempts()
        {
            var handler = new FakeHttpHandler();
            const string conflict = "{\"error\":{\"code\":\"incomplete_publication\"}}";
            handler.RespondWith("action=m3u_editor_catalog", conflict, System.Net.HttpStatusCode.Conflict);
            handler.RespondWith("action=m3u_editor_catalog", conflict, System.Net.HttpStatusCode.Conflict);
            handler.RespondWith("action=m3u_editor_catalog", conflict, System.Net.HttpStatusCode.Conflict);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCatalogAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None));

                Assert.Equal("Managed catalog request failed with HTTP 409.", error.Message);
                Assert.Equal(3, handler.ReceivedUrls.Count);
            }
        }

        [Fact]
        public async Task GetCatalogAsync_CancellationDuringConflictBackoff_IsHonored()
        {
            var handler = new FakeHttpHandler();
            const string conflict = "{\"error\":{\"code\":\"stale_revision\"}}";
            handler.RespondWith("action=m3u_editor_catalog", conflict, System.Net.HttpStatusCode.Conflict);
            handler.RespondWith("action=m3u_editor_catalog", conflict, System.Net.HttpStatusCode.Conflict);
            handler.RespondWith("action=m3u_editor_catalog", conflict, System.Net.HttpStatusCode.Conflict);
            using (var httpClient = new HttpClient(handler))
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
            {
                var client = new M3uEditorClient(httpClient);

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetCatalogAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    cancellation.Token));
            }
        }

        [Fact]
        public async Task GetCatalogAsync_UnrelatedConflict_FailsWithoutRetrying()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith(
                "action=m3u_editor_catalog",
                "{\"error\":{\"code\":\"mapping_not_found\"}}",
                System.Net.HttpStatusCode.Conflict);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCatalogAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None));

                Assert.Equal("Managed catalog request failed with HTTP 409.", error.Message);
                Assert.Single(handler.ReceivedUrls);
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

        [Theory]
        [InlineData("http://editor.example")]
        [InlineData("https://editor.example?next=https://other.example")]
        [InlineData("https://editor.example/#fragment")]
        [InlineData("https://account@editor.example")]
        public async Task ManagedRequests_NonConfinedOrigin_FailBeforeSendingCredentials(string baseUrl)
        {
            var handler = new FakeHttpHandler();
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCatalogAsync(
                    baseUrl, "account", "credential", CancellationToken.None));

                Assert.Contains("HTTPS", error.Message);
                Assert.Empty(handler.ReceivedUrls);
            }
        }

        [Fact]
        public async Task GetCatalogAsync_OversizedDeclaredResponse_FailsClosed()
        {
            var handler = new DeclaredLengthHandler(M3uEditorClient.MaximumResponseBytes + 1L);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCatalogAsync(
                    "https://editor.example", "account", "credential", CancellationToken.None));

                Assert.Contains("response limit", error.Message);
            }
        }

        [Theory]
        [InlineData("capability", "GET")]
        [InlineData("catalog", "GET")]
        [InlineData("callback", "POST")]
        public async Task ManagedRequests_IncrementalOversizedResponse_ReadsOnlyThroughSentinel(
            string requestPath,
            string expectedMethod)
        {
            var content = new IncrementalContent(M3uEditorClient.MaximumResponseBytes + 8192);
            var handler = new IncrementalContentHandler(content);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => SendManagedRequestAsync(client, requestPath));

                Assert.Contains("response limit", error.Message);
                Assert.Equal(expectedMethod, handler.Method);
                Assert.Equal(M3uEditorClient.MaximumResponseBytes + 1L, content.BytesRead);
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

        [Fact]
        public void CatalogOutputPathAcceptance_UsesCurrentRuntimeRootSemantics()
        {
            var method = typeof(M3uEditorCatalogValidator).GetMethod(
                "IsAbsolutePath",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            Func<string, bool> accepts = path => (bool)method.Invoke(null, new object[] { path });
            var nativeAbsolutePath = Path.Combine(Path.GetTempPath(), "managed-movies");
            var candidates = new[]
            {
                nativeAbsolutePath,
                @"C:\managed\movies",
                @"\managed\movies",
                @"\\server\share\movies"
            };

            Assert.True(Path.IsPathRooted(nativeAbsolutePath));
            Assert.True(accepts(nativeAbsolutePath));
            foreach (var candidate in candidates)
            {
                if (accepts(candidate))
                {
                    Assert.True(Path.IsPathRooted(candidate), "Accepted path was not rooted on this runtime: " + candidate);
                }
            }

            if (Path.DirectorySeparatorChar == '/')
            {
                Assert.False(accepts(@"C:\managed\movies"));
                Assert.False(accepts(@"\managed\movies"));
                Assert.False(accepts(@"\\server\share\movies"));
            }
        }

        [Fact]
        public void Validate_ExcessiveMappingCount_FailsClosed()
        {
            var catalog = JsonSerializer.Deserialize<M3uEditorCatalog>(CatalogJson);
            var mapping = catalog.Mappings[0];
            catalog.Mappings = Enumerable.Repeat(mapping, M3uEditorCatalogValidator.MaximumMappings + 1).ToList();

            var error = Assert.Throws<InvalidOperationException>(() => M3uEditorCatalogValidator.Validate(catalog));

            Assert.Contains("mappings", error.Message);
        }

        [Fact]
        public void Validate_NfoPlotAtFieldLimit_Accepts()
        {
            var catalog = JsonSerializer.Deserialize<M3uEditorCatalog>(CatalogJson);
            catalog.Mappings[0].Items[0].Nfo.Plot = new string('a', 256 * 1024);

            M3uEditorCatalogValidator.Validate(catalog);
        }

        [Fact]
        public void Validate_NfoPlotOneCharacterOverFieldLimit_FailsClosed()
        {
            var catalog = JsonSerializer.Deserialize<M3uEditorCatalog>(CatalogJson);
            catalog.Mappings[0].Items[0].Nfo.Plot = new string('a', (256 * 1024) + 1);

            var error = Assert.Throws<InvalidOperationException>(() => M3uEditorCatalogValidator.Validate(catalog));

            Assert.Contains("NFO metadata", error.Message);
        }

        [Fact]
        public void Validate_PlaybackUrlOneCharacterOverFieldLimit_FailsClosed()
        {
            var catalog = JsonSerializer.Deserialize<M3uEditorCatalog>(CatalogJson);
            catalog.Mappings[0].Items[0].Variants[0].Preferred.PlaybackUrl =
                "https://editor.example/" + new string('a', 8193 - "https://editor.example/".Length);

            var error = Assert.Throws<InvalidOperationException>(() => M3uEditorCatalogValidator.Validate(catalog));

            Assert.Contains("playback URL", error.Message);
        }

        [Fact]
        public void SafeFilename_EnforcesLengthLimit()
        {
            Assert.True(M3uEditorCatalogValidator.IsSafeFilename(new string('a', 240)));
            Assert.False(M3uEditorCatalogValidator.IsSafeFilename(new string('a', 241)));
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

        private static async Task SendManagedRequestAsync(M3uEditorClient client, string requestPath)
        {
            if (requestPath == "capability")
            {
                await client.DiscoverCapabilityAsync(
                    "https://editor.example", "account", "credential", CancellationToken.None);
                return;
            }

            if (requestPath == "catalog")
            {
                await client.GetCatalogAsync(
                    "https://editor.example", "account", "credential", CancellationToken.None);
                return;
            }

            await client.ReportSyncResultAsync(
                "https://editor.example",
                "account",
                "credential",
                7,
                "123e4567-e89b-12d3-a456-426614174000",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                true,
                "summary",
                null,
                CancellationToken.None);
        }

        private sealed class IncrementalContentHandler : HttpMessageHandler
        {
            private readonly HttpContent _content;

            public IncrementalContentHandler(HttpContent content)
            {
                _content = content;
            }

            public string Method { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Method = request.Method.Method;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = _content
                });
            }
        }

        private sealed class IncrementalContent : HttpContent
        {
            private readonly int _length;

            public IncrementalContent(int length)
            {
                _length = length;
            }

            public long BytesRead { get; private set; }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                using (var source = CreateStream())
                {
                    var buffer = new byte[8192];
                    int read;
                    while ((read = await source.ReadAsync(buffer, 0, buffer.Length)) != 0)
                    {
                        await stream.WriteAsync(buffer, 0, read);
                    }
                }
            }

            protected override Task<Stream> CreateContentReadStreamAsync()
            {
                return Task.FromResult<Stream>(CreateStream());
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }

            private Stream CreateStream()
            {
                return new IncrementalStream(_length, read => BytesRead += read);
            }
        }

        private sealed class IncrementalStream : Stream
        {
            private readonly int _length;
            private readonly Action<int> _recordRead;
            private int _position;

            public IncrementalStream(int length, Action<int> recordRead)
            {
                _length = length;
                _recordRead = recordRead;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position
            {
                get { return _position; }
                set { throw new NotSupportedException(); }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = Math.Min(count, _length - _position);
                if (read == 0)
                {
                    return 0;
                }

                Array.Fill(buffer, (byte)'x', offset, read);
                _position += read;
                _recordRead(read);
                return read;
            }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

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

        private sealed class DeclaredLengthHandler : HttpMessageHandler
        {
            private readonly long _length;

            public DeclaredLengthHandler(long length)
            {
                _length = length;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var content = new ByteArrayContent(new byte[0]);
                content.Headers.ContentLength = _length;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = content
                });
            }
        }
    }
}
