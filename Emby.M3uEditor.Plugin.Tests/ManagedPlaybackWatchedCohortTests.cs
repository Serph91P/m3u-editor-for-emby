using System.Collections.Generic;
using Emby.M3uEditor.Plugin.Service;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedPlaybackWatchedCohortTests
    {
        [Fact]
        public void AllSelectedProfilesWatched_MarksCohortComplete_AndUndoCancelsIt()
        {
            var cohort = new ManagedPlaybackWatchedCohort(new[] { "user-a", "user-b" });

            Assert.False(cohort.Record("episode-1", "user-a", true));
            Assert.True(cohort.Record("episode-1", "user-b", true));
            Assert.False(cohort.Record("episode-1", "user-a", false));
            Assert.False(cohort.IsComplete("episode-1"));
        }

        [Fact]
        public void CohortChange_PreservesExistingInterestsUntilNewCohortCompletes()
        {
            var cohort = new ManagedPlaybackWatchedCohort(new[] { "user-a" });
            Assert.True(cohort.Record("episode-1", "user-a", true));

            cohort.UpdateUsers(new[] { "user-a", "user-b" });

            Assert.True(cohort.IsComplete("episode-1"));
            Assert.False(cohort.Record("episode-2", "user-a", true));
            Assert.True(cohort.Record("episode-2", "user-b", true));
        }
    }
}
