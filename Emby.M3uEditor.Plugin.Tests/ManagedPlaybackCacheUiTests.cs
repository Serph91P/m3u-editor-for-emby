using System.IO;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedPlaybackCacheUiTests
    {
        [Fact]
        public void ManagedTab_ContainsOptInModesRetentionAndFailClosedStatus()
        {
            var assembly = typeof(Plugin).Assembly;
            string html;
            string javascript;
            using (var stream = assembly.GetManifestResourceStream("Emby.M3uEditor.Plugin.Configuration.Web.config.html"))
            using (var reader = new StreamReader(stream))
            {
                html = reader.ReadToEnd();
            }

            using (var stream = assembly.GetManifestResourceStream("Emby.M3uEditor.Plugin.Configuration.Web.config.js"))
            using (var reader = new StreamReader(stream))
            {
                javascript = reader.ReadToEnd();
            }

            Assert.Contains("chkManagedPlaybackCacheEnabled", html);
            Assert.Contains("value=\"off\"", html);
            Assert.Contains("value=\"next\"", html);
            Assert.Contains("value=\"next_n\"", html);
            Assert.Contains("value=\"remaining_season\"", html);
            Assert.Contains("value=\"whole_season\"", html);
            Assert.Contains("managedPlaybackCacheNextCount", html);
            Assert.Contains("managedPlaybackCacheRetention", html);
            Assert.Contains("managedPlaybackCacheStatus", html);
            Assert.DoesNotContain("ManagedApprovedOutputRoots", html);

            Assert.Contains("config.ManagedPlaybackCacheEnabled", javascript);
            Assert.Contains("config.ManagedPlaybackCacheMode", javascript);
            Assert.Contains("config.ManagedPlaybackCacheNextCount", javascript);
            Assert.Contains("config.ManagedPlaybackCacheDeleteAfterWatched", javascript);
            Assert.Contains("config.ManagedPlaybackCacheDeleteGraceHours", javascript);
            Assert.Contains("config.ManagedPlaybackCacheUserIds", javascript);
            Assert.Contains("function updateManagedPlaybackCacheVisibility", javascript);
            Assert.Contains("cacheToggle.disabled = !available", javascript);
            Assert.Contains("Cache-control capability and provider budget are verified fail-closed", javascript);
        }
    }
}
