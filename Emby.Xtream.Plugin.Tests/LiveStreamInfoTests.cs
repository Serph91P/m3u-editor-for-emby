using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Emby.Xtream.Plugin.Client.Models;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class LiveStreamInfoTests
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
        };

        [Fact]
        public void Deserialize_M3uEditorPayload_ParsesStreamStats()
        {
            // Schema observed on m3u-editor v1.x get_live_streams response.
            var json = "[{\"num\":1,\"name\":\"Channel A\",\"stream_id\":42," +
                       "\"stream_stats\":{\"resolution\":\"1920x1080\"," +
                       "\"video_codec\":\"h264\",\"audio_codec\":\"aac\"," +
                       "\"audio_channels\":\"2\",\"sample_rate\":48000," +
                       "\"audio_bitrate\":192,\"video_profile\":\"High\"," +
                       "\"video_level\":40,\"audio_language\":\"deu\"}}]";

            var list = JsonSerializer.Deserialize<List<LiveStreamInfo>>(json, Options);

            Assert.NotNull(list);
            Assert.Single(list);
            var stats = list[0].StreamStats;
            Assert.NotNull(stats);
            Assert.Equal("1920x1080", stats.Resolution);
            Assert.Equal("h264", stats.VideoCodec);
            Assert.Equal("aac", stats.AudioCodec);
            Assert.Equal("2", stats.AudioChannels);
            Assert.Equal(48000, stats.SampleRate);
            Assert.Equal("High", stats.VideoProfile);
            Assert.Equal(40, stats.VideoLevel);
            Assert.Equal("deu", stats.AudioLanguage);
        }

        [Fact]
        public void Deserialize_ChannelWithoutStreamStats_LeavesNull()
        {
            var json = "[{\"num\":1,\"name\":\"Channel A\",\"stream_id\":42}]";

            var list = JsonSerializer.Deserialize<List<LiveStreamInfo>>(json, Options);

            Assert.NotNull(list);
            Assert.Single(list);
            Assert.Null(list[0].StreamStats);
        }

        [Fact]
        public void Deserialize_AudioChannelsAsNumber_StillBindsToString()
        {
            // Dispatcharr/Streamflow sends audio_channels as a number rather
            // than a string; the converter on StreamStatsInfo must accept both.
            var json = "[{\"num\":1,\"name\":\"X\",\"stream_id\":1," +
                       "\"stream_stats\":{\"audio_channels\":6}}]";

            var list = JsonSerializer.Deserialize<List<LiveStreamInfo>>(json, Options);

            Assert.Equal("6", list[0].StreamStats.AudioChannels);
        }
    }
}
