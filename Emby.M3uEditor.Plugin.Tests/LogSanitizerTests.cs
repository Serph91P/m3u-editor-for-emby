using Emby.M3uEditor.Plugin.Service;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class LogSanitizerTests
    {
        [Fact]
        public void RedactsConfiguredCredentialsAndPersonalData()
        {
            var result = LogSanitizer.SanitizeLine(
                "User myuser password mypass at 10.0.0.1 sent mail@example.com",
                "myuser",
                "mypass");

            Assert.DoesNotContain("myuser", result);
            Assert.DoesNotContain("mypass", result);
            Assert.DoesNotContain("10.0.0.1", result);
            Assert.DoesNotContain("mail@example.com", result);
        }

        [Fact]
        public void RedactsXtreamUrlCredentialsAndProviderHost()
        {
            var result = LogSanitizer.SanitizeLine(
                "Stream URL: https://provider.example/live/john/pass123/12345.ts",
                string.Empty,
                string.Empty);

            Assert.Contains("<provider-host>", result);
            Assert.Contains("/live/<user>/<pass>/", result);
            Assert.DoesNotContain("john", result);
            Assert.DoesNotContain("pass123", result);
        }

        [Fact]
        public void RedactsQueryCredentialsApiKeysAndTokens()
        {
            var query = LogSanitizer.SanitizeLine(
                "GET /player_api.php?username=alice&password=secret&api_key=key",
                string.Empty,
                string.Empty);
            var bearer = LogSanitizer.SanitizeLine(
                "Authorization: Bearer token-value",
                string.Empty,
                string.Empty);
            var json = LogSanitizer.SanitizeLine(
                "{\"access\":\"token123\",\"refresh\":\"token456\"}",
                string.Empty,
                string.Empty);

            Assert.DoesNotContain("alice", query);
            Assert.DoesNotContain("secret", query);
            Assert.DoesNotContain("api_key=key", query);
            Assert.Contains("Bearer <redacted>", bearer);
            Assert.DoesNotContain("token123", json);
            Assert.DoesNotContain("token456", json);
        }

        [Theory]
        [InlineData("M3uEditor retired password=old-secret", "old-secret")]
        [InlineData("token: legacy-token", "legacy-token")]
        [InlineData("api_key='legacy-key'", "legacy-key")]
        [InlineData("Authorization: Basic dXNlcjpwYXNz", "dXNlcjpwYXNz")]
        [InlineData("Cookie: session=legacy-cookie", "legacy-cookie")]
        [InlineData("Set-Cookie: auth=legacy-cookie", "legacy-cookie")]
        public void RedactsGenericHistoricalSecretFormats(string input, string secret)
        {
            var result = LogSanitizer.SanitizeLine(input, string.Empty, string.Empty);

            Assert.DoesNotContain(secret, result);
            Assert.Contains("<redacted>", result);
        }

        [Theory]
        [InlineData("Loading Plugin, Version=1.2.0.0, Culture=neutral")]
        [InlineData("File Emby.dll has version 4.8.0.80")]
        public void PreservesVersionNumbers(string input)
        {
            Assert.Equal(input, LogSanitizer.SanitizeLine(input, string.Empty, string.Empty));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void HandlesNullOrEmptyLine(string input)
        {
            Assert.Equal(input, LogSanitizer.SanitizeLine(input, "user", "pass"));
        }
    }
}
