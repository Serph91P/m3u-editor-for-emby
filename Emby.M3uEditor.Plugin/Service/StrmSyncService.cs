using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Emby.M3uEditor.Plugin.Api;
using Emby.M3uEditor.Plugin.Client.Models;
using MediaBrowser.Model.Logging;
using STJ = System.Text.Json;

namespace Emby.M3uEditor.Plugin.Service
{
    public partial class StrmSyncService
    {
        private static readonly STJ.JsonSerializerOptions JsonOptions = new STJ.JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
        };

        private static readonly HttpClient ManagedHttpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly ILogger _logger;
        private readonly HttpClient _managedHttpClient;

        internal ManagedActionJobCoordinator ManagedActionJobs { get; } =
            new ManagedActionJobCoordinator(TimeSpan.FromMinutes(10));

        public StrmSyncService(ILogger logger, HttpClient httpClient = null)
        {
            _logger = logger;
            _managedHttpClient = httpClient ?? ManagedHttpClient;
        }

        internal static string ComputeChannelListHash(List<LiveStreamInfo> channels)
        {
            var sorted = channels.OrderBy(channel => channel.StreamId);
            var value = new StringBuilder();
            foreach (var channel in sorted)
            {
                value.Append(channel.StreamId);
                value.Append(':');
                value.Append(channel.Name ?? string.Empty);
                value.Append(':');
                value.Append(channel.EpgChannelId ?? string.Empty);
                value.Append(':');
                value.Append(channel.CategoryId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                value.Append(':');
                value.Append(channel.DisplayChannelNumber);
                value.Append(':');
                value.Append(channel.StreamIcon ?? string.Empty);
                value.Append('|');
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToString()));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
