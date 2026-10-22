using Emby.M3uEditor.Plugin.Service;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedPlaybackCachePolicyTests
    {
        [Fact]
        public void NextN_AppliesWatchedFilterBeforeBoundedSelection()
        {
            var episodes = new[]
            {
                new ManagedEpisodePosition { Id = "s1e1", Season = 1, Episode = 1 },
                new ManagedEpisodePosition { Id = "s1e2", Season = 1, Episode = 2 },
                new ManagedEpisodePosition { Id = "s1e3", Season = 1, Episode = 3 },
                new ManagedEpisodePosition { Id = "s1e4", Season = 1, Episode = 4 }
            };

            Assert.Equal(new[] { "s1e3", "s1e4" }, ManagedPlaybackCachePolicy.Select(
                episodes, "s1e1", "next_n", 2, new[] { "s1e2" }));
        }

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
