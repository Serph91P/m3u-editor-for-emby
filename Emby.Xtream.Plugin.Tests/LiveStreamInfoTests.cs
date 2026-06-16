using System.Collections.Generic;
using System.Linq;
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

        [Theory]
        [InlineData("2.1", "2.1")]
        [InlineData("101.5", "101.5")]
        [InlineData("6000", "6000")]
        public void Deserialize_NumAsString_PreservesDisplayChannelNumber(string input, string expected)
        {
            var json = "[{\"num\":\"" + input + "\",\"name\":\"Channel A\",\"stream_id\":42}]";

            var list = JsonSerializer.Deserialize<List<LiveStreamInfo>>(json, Options);

            Assert.Equal(expected, list[0].ChannelNumber);
            Assert.Equal(expected, list[0].DisplayChannelNumber);
        }

        [Fact]
        public void Deserialize_NumAsDecimalNumber_PreservesDecimalChannelNumber()
        {
            var json = "[{\"num\":2.1,\"name\":\"Channel A\",\"stream_id\":42}]";

            var list = JsonSerializer.Deserialize<List<LiveStreamInfo>>(json, Options);

            Assert.Equal("2.1", list[0].DisplayChannelNumber);
        }

        [Fact]
        public void MissingNum_DefaultsToZeroForBackwardCompatibility()
        {
            var json = "[{\"name\":\"Channel A\",\"stream_id\":42}]";

            var list = JsonSerializer.Deserialize<List<LiveStreamInfo>>(json, Options);

            Assert.Equal("0", list[0].DisplayChannelNumber);
            Assert.Equal(0, list[0].Num);
        }

        [Fact]
        public void ChannelNumberSortKey_OrdersDecimalNumbersNumerically()
        {
            var channels = new List<LiveStreamInfo>
            {
                new LiveStreamInfo { StreamId = 10, ChannelNumber = "10", Name = "Ten" },
                new LiveStreamInfo { StreamId = 2, ChannelNumber = "2", Name = "Two" },
                new LiveStreamInfo { StreamId = 21, ChannelNumber = "2.1", Name = "Two point one" },
            };

            var sorted = channels.OrderBy(c => c.ChannelNumberSortKey).ThenBy(c => c.StreamId).ToList();

            Assert.Equal(new[] { "2", "2.1", "10" }, sorted.Select(c => c.DisplayChannelNumber).ToArray());
        }
    }
}
