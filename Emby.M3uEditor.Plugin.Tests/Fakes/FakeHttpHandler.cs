using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.M3uEditor.Plugin.Tests.Fakes
{
    /// <summary>
    /// Intercepts HttpClient calls and returns pre-registered responses.
    /// Register responses with RespondWith() before the code under test runs.
    /// Requests with no matching registration throw InvalidOperationException.
    /// </summary>
    public sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly List<(string UrlSubstring, Queue<(string Body, HttpStatusCode Status)> Responses)> _rules
            = new List<(string, Queue<(string, HttpStatusCode)>)>();
        private readonly HashSet<string> _requestIdEchoUrls = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _unhandledRequests = new List<string>();
        private readonly object _gate = new object();

        public List<string> ReceivedUrls { get; } = new List<string>();
        public List<string> ReceivedBodies { get; } = new List<string>();

        /// <summary>Returns a consistent copy of recorded request bodies.</summary>
        public IReadOnlyList<string> ReceivedBodySnapshot()
        {
            lock (_gate)
            {
                return ReceivedBodies.ToArray();
            }
        }

        /// <summary>Fails test teardown when a background request had no response rule.</summary>
        public void AssertNoUnhandledRequests()
        {
            lock (_gate)
            {
                if (_unhandledRequests.Count != 0)
                {
                    throw new InvalidOperationException(
                        "FakeHttpHandler: unhandled requests: " + string.Join(", ", _unhandledRequests));
                }
            }
        }

        /// <summary>Register a single response for URLs containing <paramref name="urlSubstring"/>.</summary>
        public void RespondWith(string urlSubstring, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            var q = new Queue<(string, HttpStatusCode)>();
            q.Enqueue((body, status));
            lock (_gate)
            {
                _rules.Add((urlSubstring, q));
            }
        }

        /// <summary>Register multiple ordered responses for the same URL pattern.</summary>
        public void RespondWithSequence(string urlSubstring, IEnumerable<string> bodies, HttpStatusCode status = HttpStatusCode.OK)
        {
            var q = new Queue<(string, HttpStatusCode)>();
            foreach (var b in bodies)
                q.Enqueue((b, status));
            lock (_gate)
            {
                _rules.Add((urlSubstring, q));
            }
        }

        /// <summary>
        /// Register a cache-control response whose request_id is copied from the
        /// actual form body. This proves the client accepts the server response,
        /// instead of merely recording an outgoing request.
        /// </summary>
        public void RespondWithRequestIdEcho(string urlSubstring)
        {
            lock (_gate)
            {
                _requestIdEchoUrls.Add(urlSubstring);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var body = request.Content == null
                ? string.Empty
                : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            lock (_gate)
            {
                ReceivedUrls.Add(url);
                ReceivedBodies.Add(body);
                foreach (var echoUrl in _requestIdEchoUrls)
                {
                    if (url.Contains(echoUrl))
                    {
                        return Task.FromResult(CreateResponse(CreateRequestIdEcho(body), HttpStatusCode.OK));
                    }
                }

                foreach (var (urlSubstring, queue) in _rules)
                {
                    if (url.Contains(urlSubstring) && queue.Count > 0)
                    {
                        var (responseBody, status) = queue.Dequeue();
                        return Task.FromResult(CreateResponse(responseBody, status));
                    }
                }

                _unhandledRequests.Add(url);
            }

            throw new InvalidOperationException(
                $"FakeHttpHandler: no registered response for URL: {url}\n" +
                $"Register one with handler.RespondWith(\"{url}\", json)");
        }

        protected override void Dispose(bool disposing) { }

        private static HttpResponseMessage CreateResponse(string body, HttpStatusCode status)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        private static string CreateRequestIdEcho(string body)
        {
            var requestId = body.Split('&')
                .Select(part => part.Split(new[] { '=' }, 2))
                .Where(pair => pair.Length == 2 && string.Equals(pair[0], "request_id", StringComparison.Ordinal))
                .Select(pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')))
                .SingleOrDefault();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                throw new InvalidOperationException("FakeHttpHandler: cache-control form had no request_id");
            }

            return "{\"api_version\":1,\"data\":{\"request_id\":\"" + requestId +
                "\",\"duplicate\":false,\"results\":[]}}";
        }
    }
}
