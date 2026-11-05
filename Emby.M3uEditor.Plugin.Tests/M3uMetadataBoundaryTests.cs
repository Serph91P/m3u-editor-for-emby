using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Emby.M3uEditor.Plugin.Service;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class M3uMetadataBoundaryTests
    {
        private const string Name = "Télévision 日本, HD";
        private const string Guide = "guide.example";
        private const string Logo = "https://images.example.test/logo.png?a=1&b=2,3";
        private const string Group = "Öffentlich 日本, HD";
        private const string StreamUrl = "https://editor.example.test/live/user/pass/42.ts";

        public static IEnumerable<object[]> MetadataCases()
        {
            var payloads = new[]
            {
                ("A\rB\n#EXTINF:-1,extra\r\nhttps://extra.example.test/x", "A B #EXTINF:-1,extra  https://extra.example.test/x"),
                ("A\0B\tC\vD\fE\u001fF\u007fG\u0085H\u009fI", "A B C D E F G H I"),
                ("A\u2028B\u2029C", "A B C"),
                ("A\" injected=\"yes\",B", "A' injected='yes',B"),
                ("A\\\",B &quot; &amp;", "A\\',B &quot; &amp;"),
                ("A,B 日本 & query=1", "A,B 日本 & query=1"),
                ("A\r\n\u0085\u2028\u2029\" ,B", "A     ' ,B")
            };
            foreach (var field in new[] { "name", "id", "logo", "group", "number", "all" })
                foreach (var payload in payloads)
                    yield return new object[] { field, payload.Item1, payload.Item2 };
        }

        // Removing any field's export normalization must break parsed boundaries or exact values.
        [Theory]
        [MemberData(nameof(MetadataCases))]
        public async Task MetadataRemainsOneRecordWithOnlyTheIntendedAttributes(string field, string input, string expected)
        {
            var fixture = new MetadataHandler();
            if (field == "name" || field == "all") fixture.Name = input;
            if (field == "id" || field == "all") fixture.Guide = input;
            if (field == "logo" || field == "all") fixture.Logo = input;
            if (field == "group" || field == "all") fixture.Group = input;
            if (field == "number" || field == "all") fixture.Number = input;
            using var service = CreateService(fixture);
            var record = Assert.Single(Parse(await service.GetM3UPlaylistAsync(CancellationToken.None)));
            AssertRecord(record,
                field == "name" || field == "all" ? expected : Name,
                field == "id" || field == "all" ? expected : Guide,
                field == "logo" || field == "all" ? expected : Logo,
                field == "group" || field == "all" ? expected : Group,
                field == "number" || field == "all" ? "0" : "2.1");

            // The typed snapshot used by the native tuner must retain upstream metadata.
            var channel = Assert.Single(await service.GetFilteredChannelsAsync(CancellationToken.None));
            Assert.Equal(fixture.Name, channel.Name);
            Assert.Equal(fixture.Guide, channel.EpgChannelId);
            Assert.Equal(fixture.Logo, channel.StreamIcon);
            Assert.Equal(fixture.Number, channel.DisplayChannelNumber);
            Assert.Equal(fixture.Group, Assert.Single(await service.GetLiveCategoriesAsync(CancellationToken.None)).CategoryName);
        }

        [Theory]
        [InlineData("2.1", "2.1")]
        [InlineData("001.02", "001.02")]
        [InlineData("0", "0")]
        [InlineData("1234567890123456789012345678901234567890", "1234567890123456789012345678901234567890")]
        [InlineData("-1", "0")]
        [InlineData("+2", "0")]
        [InlineData("2,1", "0")]
        [InlineData("2e1", "0")]
        [InlineData("NaN", "0")]
        [InlineData("２.１", "0")]
        [InlineData("2.", "0")]
        [InlineData(".1", "0")]
        [InlineData("2.1.3", "0")]
        [InlineData("2 1", "0")]
        public async Task ChannelNumberUsesExportLocalAsciiDecimalSyntax(string number, string expected)
        {
            var fixture = new MetadataHandler { Number = number };
            using var service = CreateService(fixture);
            AssertRecord(Assert.Single(Parse(await service.GetM3UPlaylistAsync(CancellationToken.None))), Name, Guide, Logo, Group, expected);
            Assert.Equal(number, Assert.Single(await service.GetFilteredChannelsAsync(CancellationToken.None)).DisplayChannelNumber);
        }

        [Fact]
        public async Task MultipleChannelsAndCachedExportRetainExactUrlsAndOrdinaryMetadata()
        {
            using var service = CreateService(new MetadataHandler { SecondChannel = true });
            var playlist = await service.GetM3UPlaylistAsync(CancellationToken.None);
            var records = Parse(playlist);
            Assert.Equal(2, records.Count);
            AssertRecord(records[0], Name, Guide, Logo, Group, "2.1");
            Assert.Equal("https://editor.example.test/live/user/pass/43.ts", records[1].Url);
            Assert.Equal("Second", records[1].Label);
            Assert.Equal(new[] { "tvg-id", "tvg-name", "tvg-chno" }, records[1].Attributes.Keys);
            Assert.Equal("43", records[1].Attributes["tvg-id"]);
            Assert.Equal("Second", records[1].Attributes["tvg-name"]);
            Assert.Equal("3", records[1].Attributes["tvg-chno"]);
            Assert.Equal(playlist, await service.GetM3UPlaylistAsync(CancellationToken.None));
        }

        [Fact]
        public async Task M3uNormalizationDoesNotChangeXmltvValues()
        {
            var fixture = new MetadataHandler
            {
                Name = "Télé \"quoted\", 日本\u2028line\nnext",
                Guide = "guide\"quoted,\u2029id",
                Number = "2\" invalid=\"yes"
            };
            using var service = CreateService(fixture);
            var before = await service.GetXmltvEpgAsync(CancellationToken.None);
            await service.GetM3UPlaylistAsync(CancellationToken.None);
            service.InvalidateCache();
            var after = await service.GetXmltvEpgAsync(CancellationToken.None);
            Assert.Equal(before, after);
            var channel = Assert.Single(XDocument.Parse(after).Root.Elements("channel"));
            Assert.Equal(fixture.Guide, (string)channel.Attribute("id"));
            Assert.Equal(fixture.Name, (string)channel.Element("display-name"));
            Assert.Equal(Logo, (string)channel.Element("icon").Attribute("src"));
        }

        private static void AssertRecord(Record record, string name, string id, string logo, string group, string number)
        {
            Assert.Equal(StreamUrl, record.Url);
            Assert.Equal(name, record.Label);
            Assert.Equal(new[] { "tvg-id", "tvg-name", "tvg-chno", "tvg-logo", "group-title" }, record.Attributes.Keys);
            Assert.Equal(id, record.Attributes["tvg-id"]);
            Assert.Equal(name, record.Attributes["tvg-name"]);
            Assert.Equal(number, record.Attributes["tvg-chno"]);
            Assert.Equal(logo, record.Attributes["tvg-logo"]);
            Assert.Equal(group, record.Attributes["group-title"]);
        }

        // Strict line/quoted-field parser: never HTML-decode values or split at a quoted comma.
        private static List<Record> Parse(string playlist)
        {
            var lines = Regex.Split(playlist, "\\r\\n|[\\r\\n\\u0085\\u2028\\u2029]");
            Assert.Equal("", lines.Last());
            Assert.Equal("#EXTM3U", lines[0]);
            Assert.Equal(0, (lines.Length - 2) % 2);
            var records = new List<Record>();
            for (var i = 1; i < lines.Length - 1; i += 2)
            {
                Assert.DoesNotContain(lines[i], c => char.IsControl(c));
                Assert.StartsWith("#EXTINF:-1", lines[i]);
                var position = "#EXTINF:-1".Length;
                var attributes = new Dictionary<string, string>();
                while (position < lines[i].Length && lines[i][position] == ' ')
                {
                    var match = Regex.Match(lines[i].Substring(position), "^ ([a-z-]+)=\"([^\"]*)\"");
                    Assert.True(match.Success, "Malformed attribute: " + lines[i].Substring(position));
                    Assert.True(attributes.TryAdd(match.Groups[1].Value, match.Groups[2].Value), "Duplicate attribute");
                    position += match.Length;
                }
                Assert.True(position < lines[i].Length && lines[i][position] == ',', "Missing label delimiter");
                Assert.True(Uri.TryCreate(lines[i + 1], UriKind.Absolute, out var url) && url.Scheme == "https");
                records.Add(new Record { Attributes = attributes, Label = lines[i].Substring(position + 1), Url = lines[i + 1] });
            }
            return records;
        }

        private sealed class Record
        {
            public Dictionary<string, string> Attributes;
            public string Label, Url;
        }

        private static LiveTvService CreateService(MetadataHandler handler) => new LiveTvService(
            System.Reflection.DispatchProxy.Create<ILogger, LiveTvGenerationTests.SilentLogger>(),
            _ => new HttpClient(handler, false),
            () => new PluginConfiguration
            {
                BaseUrl = "https://editor.example.test", Username = "user", Password = "pass",
                IncludeAdultChannels = true, LiveTvOutputFormat = "ts", EpgSource = EpgSourceMode.Disabled
            }, () => { });

        private sealed class MetadataHandler : HttpMessageHandler
        {
            public string Name = M3uMetadataBoundaryTests.Name, Guide = M3uMetadataBoundaryTests.Guide,
                Logo = M3uMetadataBoundaryTests.Logo, Group = M3uMetadataBoundaryTests.Group, Number = "2.1";
            public bool SecondChannel;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                string body;
                if (request.RequestUri.Query.Contains("get_live_streams"))
                {
                    var channels = new List<object> { new { stream_id = 42, category_id = 1, name = Name, epg_channel_id = Guide, stream_icon = Logo, num = Number } };
                    if (SecondChannel) channels.Add(new { stream_id = 43, name = "Second", num = "3" });
                    body = JsonSerializer.Serialize(channels);
                }
                else if (request.RequestUri.Query.Contains("get_live_categories"))
                    body = JsonSerializer.Serialize(new[] { new { category_id = 1, category_name = Group } });
                else throw new InvalidOperationException("Unexpected synthetic request: " + request.RequestUri.AbsolutePath);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body)
                };
                request.RegisterForDispose(response);
                return Task.FromResult(response);
            }
        }
    }
}
