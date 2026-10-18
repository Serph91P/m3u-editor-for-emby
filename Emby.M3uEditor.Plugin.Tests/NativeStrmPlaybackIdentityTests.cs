using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Emby.M3uEditor.Plugin.Service;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class NativeStrmPlaybackIdentityTests : SyncTestBase
    {
        [Theory]
        [InlineData("foreign_url")]
        [InlineData("missing_source")]
        [InlineData("not_concrete")]
        [InlineData("foreign_item")]
        [InlineData("tampered_bytes")]
        [InlineData("missing_manifest")]
        [InlineData("duplicate_entry")]
        [InlineData("file_url")]
        public void NativeHttpSource_WithoutExactOwnedEvidence_FailsClosed(string mutation)
        {
            var root = Path.Combine(TempDir.Path, "managed");
            var path = Path.Combine(root, "Episode.strm");
            const string url = "https://editor.example.test/series/synthetic-user/synthetic-password/1.mkv?proxy=true";
            var content = url + "\n";
            Directory.CreateDirectory(root);
            File.WriteAllText(path, content);
            string hash;
            using (var sha = SHA256.Create())
                hash = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(content)).Select(value => value.ToString("x2")));
            var entry = new { RelativePath = "Episode.strm", Sha256 = hash, Length = (long)Encoding.UTF8.GetByteCount(content), CanonicalId = "episode-1", VariantKey = "variant-A" };
            var metadata = Path.Combine(root, ".m3u-editor-for-emby");
            Directory.CreateDirectory(metadata);
            var manifest = Path.Combine(metadata, "active.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new
            {
                FormatVersion = 1,
                MappingUuid = "123e4567-e89b-12d3-a456-426614174000",
                Revision = new string('a', 64),
                Files = mutation == "duplicate_entry" ? new[] { entry, entry } : new[] { entry }
            }));
            var selected = mutation == "foreign_url" ? "https://foreign.example.test/other" : url;
            if (mutation == "missing_source") selected = null;
            if (mutation == "file_url") selected = new Uri(path).AbsoluteUri;
            if (mutation == "foreign_item")
            {
                path = Path.Combine(TempDir.Path, "foreign.strm");
                File.WriteAllText(path, content);
            }
            if (mutation == "tampered_bytes")
            {
                selected = "https://foreign.example.test/changed";
                File.WriteAllText(path, selected + "\n");
            }
            if (mutation == "missing_manifest") File.Delete(manifest);

            var resolved = new ManagedPlaybackIdentityResolver().TryResolvePlayback(
                new[] { root }, selected, mutation != "not_concrete", path, out var identity);

            Assert.False(resolved);
            Assert.Null(identity);
        }
    }
}
