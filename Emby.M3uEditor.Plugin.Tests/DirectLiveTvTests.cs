using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Emby.M3uEditor.Plugin.Service;
using Emby.M3uEditor.Plugin.Tests.Fakes;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class DirectLiveTvTests
    {
        [Fact]
        public async Task M3uEditorResponses_GenerateDirectPlaylistAndEpg()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            const string channels = "[{\"num\":7,\"name\":\"News HD\",\"stream_id\":42," +
                "\"stream_icon\":\"https://editor.example/news.png\",\"epg_channel_id\":\"news.example\"," +
                "\"category_id\":5,\"stream_stats\":{\"resolution\":\"1920x1080\",\"video_codec\":\"h264\"}}]";
            var epg = "{\"epg_listings\":[{\"id\":\"programme-1\",\"epg_id\":\"news.example\"," +
                "\"title\":\"TmV3cyBBdCBOb29u\",\"description\":\"RGFpbHkgYnVsbGV0aW4=\"," +
                "\"start_timestamp\":" + (now - 60) + ",\"stop_timestamp\":" + (now + 3600) + "}]}";
            var handler = new FakeHttpHandler();
            handler.RespondWithSequence("action=get_live_streams", new[] { channels, channels });
            handler.RespondWith("action=get_live_categories", "[{\"category_id\":5,\"category_name\":\"News\"}]");
            handler.RespondWith("action=get_simple_data_table&stream_id=42", epg);
            var configuration = new PluginConfiguration
            {
                BaseUrl = "http://editor.example",
                Username = "user",
                Password = "pass",
                EnableLiveTv = true,
                EpgSource = EpgSourceMode.XtreamServer,
                IncludeAdultChannels = true,
            };

            using (var service = new LiveTvService(
                new NullLogger(),
                _ => new HttpClient(handler, false),
                () => configuration,
                () => { }))
            {
                var playlist = await service.GetM3UPlaylistAsync(CancellationToken.None);
                var xmltv = await service.GetXmltvEpgAsync(CancellationToken.None);

                Assert.Contains("tvg-id=\"news.example\"", playlist);
                Assert.Contains("group-title=\"News\"", playlist);
                Assert.Contains("http://editor.example/live/user/pass/42.ts", playlist);
                Assert.Contains("<channel id=\"news.example\">", xmltv);
                Assert.Contains("<title>News At Noon</title>", xmltv);
                Assert.Contains("channel=\"news.example\"", xmltv);
            }
        }

        [Fact]
        public async Task XmltvReExport_ProducesCompleteDocumentAcceptedByPinnedDtd()
        {
            var start = DateTimeOffset.UtcNow.AddMinutes(5);
            var stop = start.AddHours(1);
            const string channels = "[{\"num\":7,\"name\":\"Synthetic Channel\",\"stream_id\":42," +
                "\"stream_icon\":\"https://art.example.test/channel.png\",\"epg_channel_id\":\"synthetic.example\"}]";
            var sourceXml = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                @"<?xml version=""1.0"" encoding=""UTF-8""?>
<tv>
  <programme start=""{0}"" stop=""{1}"" channel=""synthetic.example"">
    <title>Synthetic programme</title>
    <desc>Synthetic description</desc>
    <icon src=""https://art.example.test/poster.jpg"" width=""500"" height=""750"" />
    <episode-num system=""m3u-editor:content-id"">episode-12</episode-num>
    <episode-num system=""m3u-editor:series-id"">series-4</episode-num>
    <previously-shown />
    <premiere />
    <new />
    <live />
    <image type=""poster"" orient=""P"">https://art.example.test/poster.jpg</image>
    <image type=""backdrop"" orient=""L"">https://art.example.test/backdrop.jpg</image>
    <image type=""still"" orient=""L"">https://art.example.test/still.jpg</image>
  </programme>
</tv>",
                start.ToString("yyyyMMddHHmmss zzz").Replace(":", string.Empty),
                stop.ToString("yyyyMMddHHmmss zzz").Replace(":", string.Empty));
            var handler = new FakeHttpHandler();
            using (var sourceStream = new MemoryStream(Encoding.UTF8.GetBytes(sourceXml)))
            {
                var sourceProgram = Assert.Single(Emby.M3uEditor.Plugin.Client.XmltvParser.Parse(sourceStream, null, null)["synthetic.example"]);
                Assert.Equal(start.ToUnixTimeSeconds(), sourceProgram.StartTimestamp);
                Assert.Equal(stop.ToUnixTimeSeconds(), sourceProgram.StopTimestamp);
            }
            using (var filteredSourceStream = new MemoryStream(Encoding.UTF8.GetBytes(sourceXml)))
            {
                Assert.Contains(
                    "synthetic.example",
                    Emby.M3uEditor.Plugin.Client.XmltvParser.Parse(
                        filteredSourceStream,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()).Keys);
            }
            handler.RespondWithSequence("action=get_live_streams", new[] { channels, channels });
            handler.RespondWith("https://guide.example.test/xmltv.xml", sourceXml);
            var logger = new NullLogger();
            var configuration = new PluginConfiguration
            {
                BaseUrl = "https://editor.example.test",
                Username = "synthetic-user",
                Password = "synthetic-password",
                EnableLiveTv = true,
                EpgSource = EpgSourceMode.CustomUrl,
                CustomEpgUrl = "https://guide.example.test/xmltv.xml",
                IncludeAdultChannels = true,
                EpgDaysToFetch = 1,
                EnableDiagnosticsLogging = true,
            };

            string xmltv;
            using (var service = new LiveTvService(
                logger,
                _ => new HttpClient(handler, false),
                () => configuration,
                () => { }))
            {
                xmltv = await service.GetXmltvEpgAsync(CancellationToken.None);
            }

            var document = XDocument.Parse(xmltv);
            var programmes = document.Root.Elements("programme");
            Assert.True(System.Linq.Enumerable.Count(programmes) == 1, xmltv + Environment.NewLine + logger.Messages + Environment.NewLine + string.Join(Environment.NewLine, handler.ReceivedUrls));
            var programme = System.Linq.Enumerable.Single(programmes);
            Assert.Equal(
                new[] { "title", "desc", "icon", "episode-num", "episode-num", "previously-shown", "premiere", "new", "image", "image", "image" },
                System.Linq.Enumerable.Select(programme.Elements(), element => element.Name.LocalName));
            Assert.Equal("https://art.example.test/poster.jpg", programme.Element("icon").Attribute("src").Value);
            Assert.Equal("500", programme.Element("icon").Attribute("width").Value);
            Assert.Equal("750", programme.Element("icon").Attribute("height").Value);
            Assert.Equal("episode-12", System.Linq.Enumerable.First(programme.Elements("episode-num")).Value);
            Assert.Equal("series-4", System.Linq.Enumerable.Last(programme.Elements("episode-num")).Value);
            Assert.DoesNotContain(programme.Elements(), element => element.Name.LocalName == "live");

            ValidateWithPinnedXmltvDtd(document);

            var invalidOldOrder = new XDocument(document);
            var invalidProgramme = Assert.Single(invalidOldOrder.Root.Elements("programme"));
            var invalidIcon = invalidProgramme.Element("icon");
            invalidIcon.Remove();
            System.Linq.Enumerable.Last(invalidProgramme.Elements("episode-num")).AddAfterSelf(invalidIcon);
            Assert.Throws<XmlException>(() => ValidateWithPinnedXmltvDtd(invalidOldOrder));
        }

        private static void ValidateWithPinnedXmltvDtd(XDocument document)
        {
            var dtdPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "xmltv", "xmltv.dtd");
            var validatedDocument = new XDocument(document);
            validatedDocument.AddFirst(new XDocumentType("tv", null, "xmltv.dtd", null));

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Parse,
                ValidationType = ValidationType.DTD,
                XmlResolver = new PinnedFileXmlResolver(dtdPath),
            };
            settings.ValidationEventHandler += (sender, args) => throw new XmlException(args.Message, args.Exception);

            using (var stringReader = new StringReader(validatedDocument.ToString(SaveOptions.DisableFormatting)))
            using (var reader = XmlReader.Create(stringReader, settings, "file:///synthetic/xmltv.xml"))
            {
                while (reader.Read()) { }
            }
        }

        private sealed class PinnedFileXmlResolver : XmlResolver
        {
            private readonly Uri _allowedDtd;

            public PinnedFileXmlResolver(string dtdPath)
            {
                _allowedDtd = new Uri(Path.GetFullPath(dtdPath));
            }

            public override System.Net.ICredentials Credentials
            {
                set { }
            }

            public override object GetEntity(Uri absoluteUri, string role, Type ofObjectToReturn)
            {
                if (absoluteUri != _allowedDtd)
                    throw new XmlException("Only the pinned local XMLTV DTD may be resolved.");

                return File.OpenRead(_allowedDtd.LocalPath);
            }

            public override Uri ResolveUri(Uri baseUri, string relativeUri)
            {
                if (string.IsNullOrEmpty(relativeUri))
                    return baseUri;
                if (string.Equals(relativeUri, "file:///synthetic/xmltv.xml", StringComparison.Ordinal))
                    return new Uri(relativeUri);
                if (!string.Equals(relativeUri, "xmltv.dtd", StringComparison.Ordinal))
                    throw new XmlException("Only the pinned local XMLTV DTD may be resolved.");

                return _allowedDtd;
            }
        }

        private sealed class NullLogger : ILogger
        {
            public string Messages { get; private set; } = string.Empty;

            public void Info(string message, params object[] paramList)
            {
                Messages += string.Format(message, paramList) + Environment.NewLine;
            }
            public void Error(string message, params object[] paramList) { }
            public void Warn(string message, params object[] paramList)
            {
                Messages += string.Format(message, paramList) + Environment.NewLine;
            }
            public void Debug(string message, params object[] paramList)
            {
                Messages += string.Format(message, paramList) + Environment.NewLine;
            }
            public void Fatal(string message, params object[] paramList) { }
            public void FatalException(string message, Exception exception, params object[] paramList) { }
            public void ErrorException(string message, Exception exception, params object[] paramList) { }
            public void LogMultiline(string message, LogSeverity severity, StringBuilder additionalContent) { }
            public void Log(LogSeverity severity, string message, params object[] paramList) { }
            public void Info(ReadOnlyMemory<char> message) { }
            public void Error(ReadOnlyMemory<char> message) { }
            public void Warn(ReadOnlyMemory<char> message) { }
            public void Debug(ReadOnlyMemory<char> message) { }
            public void Log(LogSeverity severity, ReadOnlyMemory<char> message) { }
        }
    }
}
