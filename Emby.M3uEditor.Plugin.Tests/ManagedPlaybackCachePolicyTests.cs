using Emby.M3uEditor.Plugin.Service;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedPlaybackCachePolicyTests
    {
        [Fact]
        public void Next_CrossesSeasonWithoutSelectingCurrentOrSpecials()
        {
            var episodes = new[]
            {
                new ManagedEpisodePosition { Id = "s2e1", Season = 2, Episode = 1 },
                new ManagedEpisodePosition { Id = "s1e9", Season = 1, Episode = 9 },
                new ManagedEpisodePosition { Id = "special", Season = 0, Episode = 1 }
            };

            Assert.Equal(new[] { "s2e1" },
                ManagedPlaybackCachePolicy.Select(episodes, "s1e9", "next", 1));
        }
    }
}
