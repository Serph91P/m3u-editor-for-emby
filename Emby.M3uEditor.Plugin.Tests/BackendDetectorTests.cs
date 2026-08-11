using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Service;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class BackendDetectorTests
    {
        [Fact]
        public void DetectFromXtreamResponse_M3uEditorMarker_ReturnsM3uEditor()
        {
            var json = "{\"user_info\":{\"auth\":1},\"server_info\":{\"url\":\"example\"},\"m3u_editor\":{\"version\":\"1.0\"}}";
            using (var doc = JsonDocument.Parse(json))
            {
                var detected = BackendDetector.DetectFromXtreamResponse("http://127.0.0.1:36400", doc.RootElement);
                Assert.Equal(BackendTypes.M3uEditor, detected);
            }
        }

        [Fact]
        public void TryGetPublishingCapability_ExactVersionOneContract_EnablesManagedPublishing()
        {
            var json = "{\"m3u_editor\":{\"library_publishing\":{\"api_version\":1,\"actions\":{\"catalog\":\"m3u_editor_catalog\",\"sync_result\":\"m3u_editor_sync_result\"},\"snapshot_mode\":\"full\",\"features\":[\"library_mappings\",\"variants\",\"provider_failover\",\"local_nfo\",\"revision_metadata\"]}}}";
            using (var doc = JsonDocument.Parse(json))
            {
                M3uEditorPublishingCapability capability;

                var compatible = BackendDetector.TryGetPublishingCapability(doc.RootElement, out capability);

                Assert.True(compatible);
                Assert.Equal(1, capability.ApiVersion);
                Assert.Equal("m3u_editor_catalog", capability.CatalogAction);
                Assert.Equal("m3u_editor_sync_result", capability.SyncResultAction);
                Assert.Equal("full", capability.SnapshotMode);
                Assert.Contains("provider_failover", capability.Features);
            }
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"m3u_editor\":null}")]
        [InlineData("{\"m3u_editor\":{\"library_publishing\":{\"api_version\":2}}}")]
        [InlineData("{\"m3u_editor\":{\"library_publishing\":{\"api_version\":\"1\"}}}")]
        [InlineData("{\"m3u_editor\":{\"library_publishing\":{\"api_version\":1,\"actions\":{\"catalog\":\"wrong\",\"sync_result\":\"m3u_editor_sync_result\"},\"snapshot_mode\":\"full\",\"features\":[]}}}")]
        public void TryGetPublishingCapability_AbsentUnsupportedOrMalformed_UsesGenericXtream(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                M3uEditorPublishingCapability capability;

                Assert.False(BackendDetector.TryGetPublishingCapability(doc.RootElement, out capability));
                Assert.Null(capability);
            }
        }

        [Fact]
        public void DetectFromXtreamResponse_UserInfoPayload_ReturnsXtream()
        {
            var json = "{\"user_info\":{\"auth\":1,\"status\":\"Active\"},\"server_info\":{\"url\":\"provider.example\"}}";
            using (var doc = JsonDocument.Parse(json))
            {
                var detected = BackendDetector.DetectFromXtreamResponse("http://provider.example:8080", doc.RootElement);
                Assert.Equal(BackendTypes.Xtream, detected);
            }
        }

        [Fact]
        public void DetectFromBaseUrl_DispatcharrHintInHost_ReturnsDispatcharr()
        {
            var detected = BackendDetector.DetectFromBaseUrl("http://my-dispatcharr.local:5656");
            Assert.Equal(BackendTypes.Dispatcharr, detected);
        }

        [Fact]
        public async Task DetectDispatcharrProbeAsync_DispatcharrLikeResponse_ReturnsDispatcharr()
        {
            var handler = new FakeHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"detail\":\"Authentication credentials were not provided.\"}", Encoding.UTF8, "application/json")
                });

            using (var httpClient = new HttpClient(handler))
            {
                var detected = await BackendDetector.DetectDispatcharrProbeAsync(
                    httpClient,
                    "http://dispatcharr.local:5656",
                    CancellationToken.None);

                Assert.Equal(BackendTypes.Dispatcharr, detected);
            }
        }

        [Fact]
        public async Task DetectDispatcharrProbeAsync_NotFound_ReturnsUnknown()
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

            using (var httpClient = new HttpClient(handler))
            {
                var detected = await BackendDetector.DetectDispatcharrProbeAsync(
                    httpClient,
                    "http://provider.local:8080",
                    CancellationToken.None);

                Assert.Equal(BackendTypes.Unknown, detected);
            }
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _callback;

            public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> callback)
            {
                _callback = callback;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(_callback(request));
            }
        }
    }
}
