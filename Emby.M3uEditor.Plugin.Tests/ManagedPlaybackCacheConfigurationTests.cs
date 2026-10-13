using Emby.M3uEditor.Plugin.Service;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedPlaybackCacheConfigurationTests
    {
        [Fact]
        public void Defaults_AreOptInAndXmlSafe()
        {
            var configuration = new PluginConfiguration();

            Assert.False(configuration.ManagedPlaybackCacheEnabled);
            Assert.Equal("off", configuration.ManagedPlaybackCacheMode);
            Assert.Equal(1, configuration.ManagedPlaybackCacheNextCount);
            Assert.False(configuration.ManagedPlaybackCacheDeleteAfterWatched);
            Assert.Equal(24, configuration.ManagedPlaybackCacheDeleteGraceHours);
            Assert.Empty(configuration.ManagedPlaybackCacheUserIds);
        }

        [Fact]
        public void Validate_RejectsDeleteWithoutCohort()
        {
            var configuration = new PluginConfiguration
            {
                ManagedPlaybackCacheDeleteAfterWatched = true,
                ManagedPlaybackCacheUserIds = new string[0]
            };

            Assert.False(ManagedPlaybackCacheConfiguration.IsValid(configuration));
        }

        [Theory]
        [InlineData("unknown", 1, 24)]
        [InlineData("next_n", 11, 24)]
        [InlineData("next", 1, 169)]
        public void Validate_RejectsUnknownModeOrOutOfRangeValues(string mode, int count, int graceHours)
        {
            var configuration = new PluginConfiguration
            {
                ManagedPlaybackCacheMode = mode,
                ManagedPlaybackCacheNextCount = count,
                ManagedPlaybackCacheDeleteGraceHours = graceHours
            };

            Assert.False(ManagedPlaybackCacheConfiguration.IsValid(configuration));
        }
    }
}
