using Emby.Xtream.Plugin.Util;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class UrlValidatorTests
    {
        [Theory]
        [InlineData("http://example.com/logo.png")]
        [InlineData("https://example.com/logo.png")]
        [InlineData("HTTP://example.com/x")]
        [InlineData("Https://example.com/x")]
        [InlineData("http://192.168.178.10:36400/logo-proxy/aGVsbG8/logo.png")]
        public void Accepts_HttpAndHttps(string url)
        {
            Assert.True(UrlValidator.IsValidHttpUrl(url));
            Assert.NotNull(UrlValidator.SanitizeHttpUrl(url));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\n")]
        public void Rejects_NullOrEmptyOrWhitespace(string url)
        {
            Assert.False(UrlValidator.IsValidHttpUrl(url));
            Assert.Null(UrlValidator.SanitizeHttpUrl(url));
        }

        [Theory]
        [InlineData("ftp://example.com/x")]
        [InlineData("file:///etc/passwd")]
        [InlineData("javascript:alert(1)")]
        [InlineData("data:image/png;base64,AAA")]
        [InlineData("rtsp://example.com/stream")]
        public void Rejects_NonHttpSchemes(string url)
        {
            Assert.False(UrlValidator.IsValidHttpUrl(url));
            Assert.Null(UrlValidator.SanitizeHttpUrl(url));
        }

        [Theory]
        [InlineData("/relative/path.png")]
        [InlineData("logo.png")]
        [InlineData("not a url")]
        [InlineData("http://")]
        public void Rejects_RelativeOrMalformed(string url)
        {
            Assert.False(UrlValidator.IsValidHttpUrl(url));
            Assert.Null(UrlValidator.SanitizeHttpUrl(url));
        }

        [Fact]
        public void Trims_LeadingAndTrailingWhitespace()
        {
            var input = "   http://example.com/logo.png\t";
            var sanitized = UrlValidator.SanitizeHttpUrl(input);
            Assert.Equal("http://example.com/logo.png", sanitized);
        }

        [Fact]
        public void Sanitize_ReturnsExactInputForValidUrl_NoNormalization()
        {
            // Important: we do not want Uri to lowercase scheme/host or strip trailing slashes,
            // because the URL is rendered into XMLTV verbatim. Only trim is allowed.
            var input = "https://Example.COM/Path/Logo.PNG?x=1";
            Assert.Equal(input, UrlValidator.SanitizeHttpUrl(input));
        }
    }
}
