using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client.Models;
using Emby.M3uEditor.Plugin.Service;

namespace Emby.M3uEditor.Plugin.Client
{
    internal sealed class M3uEditorClient
    {
        internal const int MaximumResponseBytes = 16 * 1024 * 1024;
        private static readonly Regex RevisionPattern = new Regex("^[a-f0-9]{64}$", RegexOptions.Compiled);
        private static readonly Regex UrlPattern = new Regex("https?://[^\\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SecretPattern = new Regex(
            "\\b(api[_-]?key|token|password|secret)\\b\\s*[:=]\\s*[^\\s,;]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ControlPattern = new Regex("[\\x00-\\x1F\\x7F]+", RegexOptions.Compiled);
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _httpClient;

        public M3uEditorClient(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<M3uEditorPublishingCapability> DiscoverCapabilityAsync(
            string baseUrl,
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            var requestUrl = BuildBaseUrl(baseUrl, username, password);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Get, requestUrl))
                        using (var response = await _httpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            timeout.Token).ConfigureAwait(false))
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                if (attempt == 0 && (int)response.StatusCode >= 500)
                                {
                                    continue;
                                }

                                return null;
                            }

                            EnsureConfinedResponse(response, requestUrl);
                            var body = await ReadLimitedBodyAsync(response, timeout.Token).ConfigureAwait(false);
                            try
                            {
                                using (var document = JsonDocument.Parse(body))
                                {
                                    M3uEditorPublishingCapability capability;
                                    return BackendDetector.TryGetPublishingCapability(document.RootElement, out capability)
                                        ? capability
                                        : null;
                                }
                            }
                            catch (JsonException)
                            {
                                return null;
                            }
                        }
                    }
                    catch (HttpRequestException) when (attempt == 0)
                    {
                    }
                    catch (HttpRequestException)
                    {
                        throw new InvalidOperationException("Managed capability request failed.");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (TaskCanceledException)
                    {
                        throw new InvalidOperationException("Managed capability request timed out.");
                    }
                }
            }

            return null;
        }

        public async Task<M3uEditorCatalog> GetCatalogAsync(
            string baseUrl,
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            var requestUrl = BuildActionUrl(
                baseUrl,
                username,
                password,
                "m3u_editor_catalog",
                "&api_version=1");

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Get, requestUrl))
                        using (var response = await _httpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            timeout.Token).ConfigureAwait(false))
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                if ((int)response.StatusCode == 409 && attempt < 2)
                                {
                                    EnsureConfinedResponse(response, requestUrl);
                                    var conflictBody = await ReadLimitedBodyAsync(response, timeout.Token).ConfigureAwait(false);
                                    if (IsRetryableCatalogConflict(conflictBody))
                                    {
                                        await Task.Delay(TimeSpan.FromMilliseconds(100d * (attempt + 1)), timeout.Token)
                                            .ConfigureAwait(false);
                                        continue;
                                    }
                                }

                                if (attempt == 0 && (int)response.StatusCode >= 500)
                                {
                                    continue;
                                }

                                throw new InvalidOperationException(
                                    "Managed catalog request failed with HTTP " + (int)response.StatusCode + ".");
                            }

                            EnsureConfinedResponse(response, requestUrl);
                            var body = await ReadLimitedBodyAsync(response, timeout.Token).ConfigureAwait(false);
                            M3uEditorCatalog catalog;
                            try
                            {
                                catalog = JsonSerializer.Deserialize<M3uEditorCatalog>(body, JsonOptions);
                            }
                            catch (JsonException ex)
                            {
                                throw new InvalidOperationException("Managed catalog response was invalid JSON.", ex);
                            }

                            if (catalog == null)
                            {
                                throw new InvalidOperationException("Managed catalog response was empty.");
                            }

                            M3uEditorCatalogValidator.Validate(catalog);
                            return catalog;
                        }
                    }
                    catch (HttpRequestException) when (attempt == 0)
                    {
                    }
                    catch (HttpRequestException)
                    {
                        throw new InvalidOperationException("Managed catalog request failed.");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (TaskCanceledException)
                    {
                        throw new InvalidOperationException("Managed catalog request timed out.");
                    }
                }
            }

            throw new InvalidOperationException("Managed catalog request failed.");
        }

        public async Task RegisterPublisherAsync(
            string baseUrl,
            string username,
            string password,
            string registerPublisherAction,
            int integrationId,
            IEnumerable<string> writablePaths,
            CancellationToken cancellationToken)
        {
            var paths = writablePaths == null
                ? new List<string>()
                : new List<string>(writablePaths);
            if (!string.Equals(
                    registerPublisherAction,
                    "m3u_editor_register_publisher",
                    StringComparison.Ordinal) ||
                integrationId < 1 ||
                paths.Count < 1 ||
                paths.Count > 50 ||
                paths.Exists(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException("Managed publisher registration is invalid.");
            }

            var requestUrl = BuildActionUrl(
                baseUrl,
                username,
                password,
                registerPublisherAction,
                string.Empty);
            var fields = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("api_version", "1"),
                new KeyValuePair<string, string>(
                    "integration_id",
                    integrationId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            };
            for (var index = 0; index < paths.Count; index++)
            {
                fields.Add(new KeyValuePair<string, string>(
                    "writable_paths[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]",
                    paths[index]));
            }

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var content = new FormUrlEncodedContent(fields))
            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                request.Content = content;
                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                    throw new InvalidOperationException("Managed publisher registration request failed.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (TaskCanceledException)
                {
                    throw new InvalidOperationException("Managed publisher registration request timed out.");
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            "Managed publisher registration request failed with HTTP " +
                            (int)response.StatusCode + ".");
                    }

                    EnsureConfinedResponse(response, requestUrl);
                    await ReadLimitedBodyAsync(response, timeout.Token).ConfigureAwait(false);
                }
            }
        }

        private static bool IsRetryableCatalogConflict(string body)
        {
            try
            {
                using (var document = JsonDocument.Parse(body))
                {
                    JsonElement error;
                    JsonElement code;
                    if (!document.RootElement.TryGetProperty("error", out error) ||
                        error.ValueKind != JsonValueKind.Object ||
                        !error.TryGetProperty("code", out code) ||
                        code.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    var value = code.GetString();
                    return string.Equals(value, "stale_revision", StringComparison.Ordinal) ||
                           string.Equals(value, "incomplete_publication", StringComparison.Ordinal);
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        public async Task<M3uEditorSyncResult> ReportSyncResultAsync(
            string baseUrl,
            string username,
            string password,
            int integrationId,
            string mappingUuid,
            string revision,
            bool success,
            string summary,
            string error,
            CancellationToken cancellationToken)
        {
            Guid parsedMappingUuid;
            if (integrationId < 1 || !Guid.TryParse(mappingUuid, out parsedMappingUuid) ||
                string.IsNullOrEmpty(revision) || !RevisionPattern.IsMatch(revision))
            {
                throw new InvalidOperationException("Managed sync result identity is invalid.");
            }

            var requestUrl = BuildActionUrl(
                baseUrl,
                username,
                password,
                "m3u_editor_sync_result",
                string.Empty);
            var fields = new Dictionary<string, string>
            {
                ["api_version"] = "1",
                ["integration_id"] = integrationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["mapping_uuid"] = mappingUuid,
                ["revision"] = revision,
                ["status"] = success ? "success" : "failed",
                ["summary"] = RedactCallbackText(summary, username, password),
                ["error"] = RedactCallbackText(error, username, password)
            };

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var content = new FormUrlEncodedContent(fields))
            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                request.Content = content;
                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                    throw new InvalidOperationException("Managed sync result request failed.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (TaskCanceledException)
                {
                    throw new InvalidOperationException("Managed sync result request timed out.");
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        if (!success && (int)response.StatusCode == 422)
                        {
                            return new M3uEditorSyncResult
                            {
                                Applied = false,
                                Duplicate = false,
                                MappingUuid = mappingUuid,
                                Revision = revision
                            };
                        }

                        throw new InvalidOperationException(
                            "Managed sync result request failed with HTTP " + (int)response.StatusCode + ".");
                    }

                    EnsureConfinedResponse(response, requestUrl);
                    var body = await ReadLimitedBodyAsync(response, timeout.Token).ConfigureAwait(false);
                    M3uEditorResponse<M3uEditorSyncResult> envelope;
                    try
                    {
                        envelope = JsonSerializer.Deserialize<M3uEditorResponse<M3uEditorSyncResult>>(body, JsonOptions);
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidOperationException("Managed sync result response was invalid JSON.", ex);
                    }

                    var result = envelope == null ? null : envelope.Data;
                    if (envelope == null || envelope.ApiVersion != 1 || result == null || !result.Applied ||
                        !string.Equals(result.MappingUuid, mappingUuid, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(result.Revision, revision, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Managed sync result response identity is invalid.");
                    }

                    return result;
                }
            }
        }

        private static string BuildActionUrl(
            string baseUrl,
            string username,
            string password,
            string action,
            string suffix)
        {
            var validatedBaseUrl = ValidateBaseUrl(baseUrl);
            return validatedBaseUrl + "/player_api.php?username=" + Uri.EscapeDataString(username ?? string.Empty) +
                   "&password=" + Uri.EscapeDataString(password ?? string.Empty) +
                   "&action=" + Uri.EscapeDataString(action) + suffix;
        }

        private static string BuildBaseUrl(string baseUrl, string username, string password)
        {
            var validatedBaseUrl = ValidateBaseUrl(baseUrl);
            return validatedBaseUrl + "/player_api.php?username=" + Uri.EscapeDataString(username ?? string.Empty) +
                   "&password=" + Uri.EscapeDataString(password ?? string.Empty);
        }

        private static string ValidateBaseUrl(string baseUrl)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(baseUrl) ||
                !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttps &&
                 (uri.Scheme != Uri.UriSchemeHttp || !IsTrustedHttpHost(uri.Host))) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            {
                throw new InvalidOperationException("The managed backend base URL is not an allowed explicit origin.");
            }

            return baseUrl.Trim().TrimEnd('/');
        }

        private static bool IsTrustedHttpHost(string host)
        {
            IPAddress address;
            if (IPAddress.TryParse(host, out address))
            {
                if (IPAddress.IsLoopback(address))
                {
                    return true;
                }

                var bytes = address.GetAddressBytes();
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return bytes[0] == 10 ||
                           (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                           (bytes[0] == 192 && bytes[1] == 168) ||
                           (bytes[0] == 169 && bytes[1] == 254);
                }

                return address.AddressFamily == AddressFamily.InterNetworkV6 &&
                       ((bytes[0] & 0xfe) == 0xfc ||
                        (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80));
            }

            return host.IndexOf('.') < 0 &&
                   Uri.CheckHostName(host) == UriHostNameType.Dns;
        }

        private static void EnsureConfinedResponse(HttpResponseMessage response, string requestUrl)
        {
            var status = (int)response.StatusCode;
            if (status >= 300 && status < 400)
            {
                throw new InvalidOperationException("Managed backend redirects are not allowed.");
            }

            var actual = response.RequestMessage == null ? null : response.RequestMessage.RequestUri;
            Uri requested;
            if (actual != null && Uri.TryCreate(requestUrl, UriKind.Absolute, out requested) &&
                (!string.Equals(actual.Scheme, requested.Scheme, StringComparison.OrdinalIgnoreCase) ||
                  !string.Equals(actual.Host, requested.Host, StringComparison.OrdinalIgnoreCase) ||
                  actual.Port != requested.Port))
            {
                throw new InvalidOperationException("Managed backend response origin changed.");
            }
        }

        private static async Task<string> ReadLimitedBodyAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            var declared = response.Content.Headers.ContentLength;
            if (declared.HasValue && declared.Value > MaximumResponseBytes)
            {
                throw new InvalidOperationException("Managed backend response limit exceeded.");
            }

            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[8192];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remaining = MaximumResponseBytes - buffer.Length;
                    var readSize = (int)Math.Min(chunk.Length, remaining + 1);
                    var read = await stream.ReadAsync(chunk, 0, readSize, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (buffer.Length + read > MaximumResponseBytes)
                    {
                        throw new InvalidOperationException("Managed backend response limit exceeded.");
                    }

                    buffer.Write(chunk, 0, read);
                }

                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        private static string RedactCallbackText(string value, string username, string password)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var redacted = value;
            if (!string.IsNullOrEmpty(username))
            {
                redacted = redacted.Replace(username, "[redacted]");
            }

            if (!string.IsNullOrEmpty(password))
            {
                redacted = redacted.Replace(password, "[redacted]");
            }

            redacted = UrlPattern.Replace(redacted, "[redacted-url]");
            redacted = SecretPattern.Replace(redacted, "$1=[redacted]");
            redacted = ControlPattern.Replace(redacted, " ").Trim();
            return redacted.Length <= 2000 ? redacted : redacted.Substring(0, 2000);
        }
    }
}
