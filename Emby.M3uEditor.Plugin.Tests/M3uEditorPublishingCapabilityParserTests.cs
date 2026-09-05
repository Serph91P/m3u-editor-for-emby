using System.Text.Json;
using Emby.M3uEditor.Plugin.Service;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class M3uEditorPublishingCapabilityParserTests
    {
        [Fact]
        public void TryGetPublishingCapability_ExactVersionOneContract_EnablesManagedPublishing()
        {
            var json = "{\"m3u_editor\":{\"library_publishing\":{\"api_version\":1,\"actions\":{\"register_publisher\":\"m3u_editor_register_publisher\",\"catalog\":\"m3u_editor_catalog\",\"sync_result\":\"m3u_editor_sync_result\"},\"snapshot_mode\":\"full\",\"features\":[\"library_mappings\",\"variants\",\"provider_failover\",\"local_nfo\",\"revision_metadata\"]}}}";
            using (var doc = JsonDocument.Parse(json))
            {
                var compatible = M3uEditorPublishingCapabilityParser.TryGetPublishingCapability(
                    doc.RootElement,
                    out var capability);

                Assert.True(compatible);
                Assert.Equal(1, capability.ApiVersion);
                Assert.Equal("m3u_editor_register_publisher", capability.RegisterPublisherAction);
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
        [InlineData("{\"m3u_editor\":{\"library_publishing\":{\"api_version\":1,\"actions\":{\"catalog\":\"m3u_editor_catalog\",\"sync_result\":\"m3u_editor_sync_result\"},\"snapshot_mode\":\"full\",\"features\":[\"library_mappings\",\"variants\",\"provider_failover\",\"local_nfo\",\"revision_metadata\"]}}}")]
        [InlineData("{\"m3u_editor\":{\"library_publishing\":{\"api_version\":1,\"actions\":{\"register_publisher\":\"remote_action\",\"catalog\":\"m3u_editor_catalog\",\"sync_result\":\"m3u_editor_sync_result\"},\"snapshot_mode\":\"full\",\"features\":[\"library_mappings\",\"variants\",\"provider_failover\",\"local_nfo\",\"revision_metadata\"]}}}")]
        public void TryGetPublishingCapability_AbsentUnsupportedOrMalformed_IsRejected(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                Assert.False(M3uEditorPublishingCapabilityParser.TryGetPublishingCapability(
                    doc.RootElement,
                    out var capability));
                Assert.Null(capability);
            }
        }
    }
}
