using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client.Models;

namespace Emby.M3uEditor.Plugin.Client
{
    internal sealed class PreservedPrimaryClient
    {
        private const int MaximumPageBytes = 512 * 1024;
        private const long MaximumWindow = 14 * 86400;
        private readonly Func<int, HttpClient> _httpClientFactory;

        internal PreservedPrimaryClient(Func<int, HttpClient> httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        // Null means unavailable/incomplete, not an empty successful snapshot.
        internal async Task<List<PreservedPrimaryEntry>> FetchAsync(PluginConfiguration config,
            int[] streamIds, long start, long stop, string format, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (start < 0 || stop <= start || (format != "xmltv" && format != "json")
                || streamIds == null || streamIds.Any(id => id <= 0)) return null;
            try
            {
                var result = new List<PreservedPrimaryEntry>();
                var ids = streamIds.Distinct().ToArray();
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                using (var client = _httpClientFactory(10))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(120));
                    for (var offset = 0; offset < ids.Length; offset += 100)
                    {
                        var batch = ids.Skip(offset).Take(100).ToArray();
                        for (var from = start; from < stop;)
                        {
                            var to = from + Math.Min(MaximumWindow, stop - from);
                            var entries = await FetchBatchAsync(client, config, batch, from, to, format, timeout.Token).ConfigureAwait(false);
                            if (entries == null) return null;
                            result.AddRange(entries);
                            from = to;
                        }
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                // Programmes spanning adjacent windows are returned by both; retain one exact record.
                return result.GroupBy(e => JsonSerializer.Serialize(e), StringComparer.Ordinal).Select(g => g.First()).ToList();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is OperationCanceledException || ex is HttpRequestException
                || ex is JsonException || ex is InvalidOperationException || ex is IOException || ex is ArgumentException)
            {
                return null;
            }
        }

        private static async Task<List<PreservedPrimaryEntry>> FetchBatchAsync(HttpClient client,
            PluginConfiguration config, int[] ids, long start, long stop, string format, CancellationToken token)
        {
            var entries = new List<PreservedPrimaryEntry>();
            var seenEntries = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string cursor = null, snapshot = null;
            var total = -1;
            do
            {
                var payload = JsonSerializer.Serialize(new { stream_ids = ids, start_timestamp = start,
                    stop_timestamp = stop, format, cursor });
                using (var request = new HttpRequestMessage(HttpMethod.Post, config.BaseUrl + "/player_api.php"))
                {
                    request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                        ["username"] = config.Username ?? string.Empty, ["password"] = config.Password ?? string.Empty,
                        ["action"] = "plugin_query", ["plugin_id"] = "epg-enricher", ["query"] = "preserved_primary_v1", ["payload"] = payload });
                    if ((await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Length > 128 * 1024) return null;
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) return null;
                        if (response.Content.Headers.ContentLength > MaximumPageBytes) return null;
                        using (var body = new LimitedResponseStream(await response.Content.ReadAsStreamAsync().ConfigureAwait(false), MaximumPageBytes))
                        using (var doc = await JsonDocument.ParseAsync(body, cancellationToken: token).ConfigureAwait(false))
                        {
                            token.ThrowIfCancellationRequested();
                            var root = doc.RootElement;
                            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)
                                || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("total", out var count)
                                || !count.TryGetInt32(out var pageTotal) || pageTotal < 0
                                || !data.TryGetProperty("next_cursor", out _)) return null;
                            if (!HasFields(data, "snapshot_id", "total", "next_cursor", "entries")
                                || data.GetProperty("entries").ValueKind != JsonValueKind.Array) return null;
                            foreach (var row in data.GetProperty("entries").EnumerateArray())
                            {
                                if (!HasFields(row, "stream_id", "start_timestamp", "stop_timestamp", "title", "subtitle", "guide", "primary")
                                    || !HasFields(row.GetProperty("guide"), "primary_icon_url", "poster_url", "poster_width", "poster_height",
                                        "backdrop_url", "still_url", "still_width", "still_height")
                                    || !HasFields(row.GetProperty("primary"), "url", "width", "height")) return null;
                            }
                            var envelope = JsonSerializer.Deserialize<PreservedPrimaryProjection>(root.GetRawText());
                            var page = envelope.Data;
                            if (envelope.ApiVersion != 1 || envelope.PluginId != "epg-enricher" || envelope.Query != "preserved_primary_v1"
                                || page == null || string.IsNullOrEmpty(page.SnapshotId) || page.Entries == null || page.Entries.Count > 256) return null;
                            if (total < 0) { total = pageTotal; snapshot = page.SnapshotId; }
                            if (pageTotal != total || snapshot != page.SnapshotId) return null;
                            foreach (var entry in page.Entries)
                            {
                                if (entry == null || !ids.Contains(entry.StreamId) || entry.StartTimestamp < 0
                                    || entry.StopTimestamp <= entry.StartTimestamp || entry.StopTimestamp <= start || entry.StartTimestamp >= stop
                                    || entry.Title == null || entry.Guide == null || entry.Primary == null
                                    || !ExactHttpUrl(entry.Primary.Url) || entry.Primary.Width < 0 || entry.Primary.Height < 0
                                    || !seenEntries.Add(JsonSerializer.Serialize(entry))) return null;
                                entries.Add(entry);
                            }
                            if (entries.Count > total) return null;
                            cursor = page.NextCursor;
                            if (cursor != null && (page.Entries.Count == 0 || entries.Count >= total
                                || !Regex.IsMatch(cursor, "\\A[a-f0-9]{64}\\z") || !cursors.Add(cursor))) return null;
                        }
                    }
                }
            } while (cursor != null);
            return entries.Count == total ? entries : null;
        }

        private static bool HasFields(JsonElement value, params string[] names)
        {
            return value.ValueKind == JsonValueKind.Object && names.All(name => value.TryGetProperty(name, out _));
        }

        internal static bool ExactHttpUrl(string value)
        {
            return value != null && value == Util.UrlValidator.SanitizeHttpUrl(value);
        }
    }
}
