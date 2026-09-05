using System.Collections.Generic;
using Emby.M3uEditor.Plugin.Client.Models;
using Emby.M3uEditor.Plugin.Service;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class StrmSyncServiceTests
    {
        [Fact]
        public void ComputeChannelListHash_SameChannelsDifferentOrder_SameHash()
        {
            var first = new List<LiveStreamInfo>
            {
                Channel(1, "BBC One", "bbc1", 10),
                Channel(2, "ITV", "itv", 10),
            };
            var second = new List<LiveStreamInfo>
            {
                Channel(2, "ITV", "itv", 10),
                Channel(1, "BBC One", "bbc1", 10),
            };

            Assert.Equal(
                StrmSyncService.ComputeChannelListHash(first),
                StrmSyncService.ComputeChannelListHash(second));
        }

        [Fact]
        public void ComputeChannelListHash_UserVisibleMetadataChanges_ChangeHash()
        {
            var baseline = new List<LiveStreamInfo> { Channel(1, "BBC One", "bbc1", 10, 1, "old.png") };

            Assert.NotEqual(Hash(baseline), Hash(new List<LiveStreamInfo> { Channel(1, "BBC Two", "bbc1", 10, 1, "old.png") }));
            Assert.NotEqual(Hash(baseline), Hash(new List<LiveStreamInfo> { Channel(1, "BBC One", "bbc2", 10, 1, "old.png") }));
            Assert.NotEqual(Hash(baseline), Hash(new List<LiveStreamInfo> { Channel(1, "BBC One", "bbc1", 11, 1, "old.png") }));
            Assert.NotEqual(Hash(baseline), Hash(new List<LiveStreamInfo> { Channel(1, "BBC One", "bbc1", 10, 2, "old.png") }));
            Assert.NotEqual(Hash(baseline), Hash(new List<LiveStreamInfo> { Channel(1, "BBC One", "bbc1", 10, 1, "new.png") }));
        }

        [Fact]
        public void ComputeChannelListHash_EmptyAndNullMetadata_AreStable()
        {
            var empty = new List<LiveStreamInfo>();
            var channels = new List<LiveStreamInfo> { Channel(1, null, null, null, 0, null) };

            Assert.NotEmpty(Hash(empty));
            Assert.Equal(Hash(empty), Hash(empty));
            Assert.Equal(Hash(channels), Hash(channels));
        }

        private static string Hash(List<LiveStreamInfo> channels)
        {
            return StrmSyncService.ComputeChannelListHash(channels);
        }

        private static LiveStreamInfo Channel(
            int streamId,
            string name,
            string epgId,
            int? categoryId,
            int number = 0,
            string icon = null)
        {
            return new LiveStreamInfo
            {
                StreamId = streamId,
                Name = name,
                EpgChannelId = epgId,
                CategoryId = categoryId,
                Num = number,
                StreamIcon = icon,
            };
        }
    }
}
