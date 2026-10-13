using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Service;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedPlaybackIdentityResolverTests : SyncTestBase
    {
        [Fact]
        public async Task Resolve_ActiveSelectedManagedSource_ReturnsManifestIdentity()
        {
            var source = await PublishSingleManagedSourceAsync();

            var identity = Resolve(source.Path, source.Hash, true);

            Assert.NotNull(identity);
            Assert.Equal(source.MappingUuid, ReadProperty(identity, "MappingUuid"));
            Assert.Equal(source.Revision, ReadProperty(identity, "Revision"));
            Assert.Equal("movie:tmdb:603", ReadProperty(identity, "CanonicalId"));
            Assert.Equal("v00", ReadProperty(identity, "VariantKey"));
        }

        [Theory]
        [InlineData(true, false, false, false)]
        [InlineData(false, true, false, false)]
        [InlineData(false, false, true, false)]
        [InlineData(false, false, false, true)]
        public async Task Resolve_ForeignStaleHashOrAmbiguousSelection_FailsClosed(
            bool foreignPath,
            bool staleGeneration,
            bool wrongHash,
            bool ambiguousSelection)
        {
            var source = await PublishSingleManagedSourceAsync();
            var path = foreignPath ? Path.Join(TempDir.Path, "foreign.strm") : source.Path;
            var hash = wrongHash
                ? "0000000000000000000000000000000000000000000000000000000000000000"
                : source.Hash;
            if (foreignPath)
            {
                File.WriteAllText(path, "https://foreign.example/stream\n");
            }

            if (staleGeneration)
            {
                File.Delete(Path.Join(TempDir.Path, ".m3u-editor-for-emby", "active.json"));
            }

            Assert.Null(Resolve(path, hash, !ambiguousSelection));
        }

        [Fact]
        public async Task Resolve_SymbolicLinkInsideManagedRoot_FailsClosedWithoutReadingTarget()
        {
            var source = await PublishSingleManagedSourceAsync();
            var outside = Path.Join(Path.GetTempPath(), "managed-playback-escape-" + Guid.NewGuid().ToString("N") + ".strm");
            File.WriteAllText(outside, "https://foreign.example/stream\n");
            try
            {
                File.Delete(source.Path);
                try
                {
                    File.CreateSymbolicLink(source.Path, outside);
                }
                catch (PlatformNotSupportedException)
                {
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }

                Assert.Null(Resolve(source.Path, source.Hash, true));
            }
            finally
            {
                File.Delete(outside);
            }
        }

        private Task<PublishedSource> PublishSingleManagedSourceAsync()
        {
            const string relativePath = "Movie/Movie - v00.strm";
            const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string mappingUuid = "123e4567-e89b-12d3-a456-426614174000";
            const string revision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var path = Path.Join(TempDir.Path, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "https://editor.example/play/0\n");
            var metadata = Path.Join(TempDir.Path, ".m3u-editor-for-emby");
            Directory.CreateDirectory(metadata);
            File.WriteAllText(Path.Join(metadata, "active.json"), JsonSerializer.Serialize(new
            {
                FormatVersion = 1,
                MappingUuid = mappingUuid,
                Revision = revision,
                Files = new[]
                {
                    new
                    {
                        RelativePath = relativePath,
                        Sha256 = hash,
                        Length = 30L,
                        CanonicalId = "movie:tmdb:603",
                        VariantKey = "v00",
                        SourceId = 1,
                        PlaylistId = 2
                    }
                }
            }));
            return Task.FromResult(new PublishedSource
            {
                Path = path,
                Hash = hash,
                MappingUuid = mappingUuid,
                Revision = revision
            });
        }

        private object Resolve(string selectedPath, string selectedHash, bool selectedMediaSourceIsConcrete)
        {
            var resolverType = typeof(StrmSyncService).Assembly.GetType(
                "Emby.M3uEditor.Plugin.Service.ManagedPlaybackIdentityResolver");
            Assert.NotNull(resolverType);
            var method = resolverType.GetMethod("TryResolve", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(method);
            var resolver = Activator.CreateInstance(resolverType);
            var arguments = new object[] { TempDir.Path, selectedPath, selectedHash, selectedMediaSourceIsConcrete, null };

            var resolved = (bool)method.Invoke(resolver, arguments);
            return resolved ? arguments[4] : null;
        }

        private static string ReadProperty(object value, string name)
        {
            var property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(property);
            return property.GetValue(value) as string;
        }

        private sealed class PublishedSource
        {
            public string Path { get; set; }
            public string Hash { get; set; }
            public string MappingUuid { get; set; }
            public string Revision { get; set; }
        }
    }
}
