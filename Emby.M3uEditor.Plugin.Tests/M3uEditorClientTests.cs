using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Client.Models;
using Emby.M3uEditor.Plugin.Service;
using Emby.M3uEditor.Plugin.Tests.Fakes;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class M3uEditorClientTests
    {
        [Fact]
        public async Task GetCatalogAsync_ExactVersionOnePayload_DeserializesVariantsAndFailover()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("action=m3u_editor_catalog", CatalogJson);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var catalog = await client.GetCatalogAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None);

                Assert.Equal(1, catalog.ApiVersion);
                Assert.True(catalog.FullSnapshot);
                Assert.Equal(Revision, catalog.Revision);
                var mapping = Assert.Single(catalog.Mappings);
                Assert.Equal("movies", mapping.TargetLibrary.CollectionType);
                var movie = Assert.Single(mapping.Items);
                Assert.Equal("movie", movie.MediaType);
                var variant = Assert.Single(movie.Variants);
                Assert.Equal("1080p-sdr-h264-aac-en-theatrical", variant.Key);
                Assert.Equal("https://editor.example/play/primary", variant.Preferred.PlaybackUrl);
                Assert.Equal("https://editor.example/play/backup", Assert.Single(variant.Failover).PlaybackUrl);
            }
        }

        [Theory]
        [MemberData(nameof(InvalidCatalogs))]
        public async Task GetCatalogAsync_InvalidContract_FailsClosedWithoutSecrets(string json, string expectedMessage)
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("action=m3u_editor_catalog", json);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var error = await Assert.ThrowsAsync<System.InvalidOperationException>(() => client.GetCatalogAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None));

                Assert.Contains(expectedMessage, error.Message);
                Assert.DoesNotContain("account", error.ToString());
                Assert.DoesNotContain("credential", error.ToString());
                Assert.DoesNotContain("https://", error.ToString());
            }
        }

        [Fact]
        public async Task DiscoverCapabilityAsync_CompatibleAdvertisement_ReturnsVersionOneCapability()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("player_api.php?username=", CapabilityJson);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var capability = await client.DiscoverCapabilityAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None);

                Assert.NotNull(capability);
                Assert.Equal(1, capability.ApiVersion);
            }
        }

        [Fact]
        public async Task LiveTvStream_SameOriginRedirect_FollowsAndCopiesPayload()
        {
            const string payload = "redirected-live-stream";
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var baseUrl = "http://127.0.0.1:" + port + "/";
            var serverTask = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient connection;
                    try
                    {
                        connection = await listener.AcceptTcpClientAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (SocketException)
                    {
                        return;
                    }

                    using (connection)
                    using (var stream = connection.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                    {
                        var requestLine = await reader.ReadLineAsync();
                        await ReadRequestHeadersAsync(reader);

                        var response = requestLine.Contains("/live-stream")
                            ? Encoding.ASCII.GetBytes(
                                "HTTP/1.1 302 Found\r\nLocation: " + baseUrl +
                                "redirected-stream\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                            : Encoding.ASCII.GetBytes(
                                "HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nContent-Length: " +
                                payload.Length + "\r\nConnection: close\r\n\r\n" + payload);
                        await stream.WriteAsync(response, 0, response.Length);
                    }
                }
            });

            try
            {
                var instanceField = typeof(Plugin).GetField(
                    "_instance",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var previousInstance = Plugin.InstanceOrNull;
                var testInstance = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
                for (var type = typeof(Plugin); type != null; type = type.BaseType)
                {
                    foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (field.FieldType == typeof(object) && field.GetValue(testInstance) == null)
                            field.SetValue(testInstance, new object());
                        if (field.FieldType == typeof(PluginConfiguration) && field.GetValue(testInstance) == null)
                            field.SetValue(testInstance, new PluginConfiguration());
                    }
                }
                instanceField.SetValue(null, testInstance);
                try
                {
                    var mediaSource = new MediaSourceInfo
                    {
                        Id = "live-stream",
                        Path = baseUrl + "live-stream"
                    };
                    using (var liveStream = new M3uEditorLiveStream(
                        mediaSource,
                        "tuner",
                        Plugin.CreateHttpClient(userAgentOverride: "redirect-regression-test")))
                    using (var output = new MemoryStream())
                    {
                        await liveStream.CopyToAsync(output, null, null, CancellationToken.None);

                        Assert.Equal(payload, Encoding.ASCII.GetString(output.ToArray()));
                    }
                }
                finally
                {
                    instanceField.SetValue(null, previousInstance);
                }
            }
            finally
            {
                listener.Stop();
                await serverTask;
            }
        }

        [Fact]
        public async Task DiscoverCapabilityAsync_SameOriginRedirect_DoesNotRequestRedirectTarget()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var baseUrl = "http://127.0.0.1:" + port + "/";
            var redirectedRequests = 0;
            var serverTask = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient connection;
                    try
                    {
                        connection = await listener.AcceptTcpClientAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (SocketException)
                    {
                        return;
                    }

                    using (connection)
                    using (var stream = connection.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                    {
                        var requestLine = await reader.ReadLineAsync();
                        await ReadRequestHeadersAsync(reader);

                        byte[] response;
                        if (requestLine.Contains("/player_api.php"))
                        {
                            response = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 302 Found\r\nLocation: " + baseUrl +
                                "redirect-target\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        }
                        else
                        {
                            Interlocked.Increment(ref redirectedRequests);
                            var body = Encoding.UTF8.GetBytes(CapabilityJson);
                            response = Encoding.UTF8.GetBytes(
                                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " +
                                body.Length + "\r\nConnection: close\r\n\r\n" + CapabilityJson);
                        }

                        await stream.WriteAsync(response, 0, response.Length);
                    }
                }
            });

            try
            {
                var service = new StrmSyncService(null);
                var result = await service.ReconcileManagedAsync(
                    new PluginConfiguration
                    {
                        BaseUrl = baseUrl,
                        Username = "account",
                        Password = "credential"
                    },
                    null,
                    null,
                    CancellationToken.None,
                    null);

                Assert.False(result.Compatible);
                Assert.Equal(0, Volatile.Read(ref redirectedRequests));
            }
            finally
            {
                listener.Stop();
                await serverTask;
            }
        }

        [Fact]
        public async Task ManagedRequests_ConfiguredProxy_IsBypassedForCredentialBearingRequest()
        {
            using var proxyListener = new TcpListener(IPAddress.Loopback, 0);
            using var targetListener = new TcpListener(IPAddress.Loopback, 0);
            proxyListener.Start();
            targetListener.Start();
            var proxyPort = ((IPEndPoint)proxyListener.LocalEndpoint).Port;
            var targetPort = ((IPEndPoint)targetListener.LocalEndpoint).Port;
            var proxyRequests = 0;
            var credentialBearingProxyRequests = 0;
            var targetRequests = 0;
            var responseBody = Encoding.UTF8.GetBytes(CapabilityJson);
            var responseHeaders = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " +
                responseBody.Length + "\r\nConnection: close\r\n\r\n");

            Func<TcpListener, Action<string>, Task> serve = async (listener, recordRequest) =>
            {
                while (true)
                {
                    TcpClient connection;
                    try
                    {
                        connection = await listener.AcceptTcpClientAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (SocketException)
                    {
                        return;
                    }

                    using (connection)
                    using (var stream = connection.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                    {
                        var requestLine = await reader.ReadLineAsync();
                        await ReadRequestHeadersAsync(reader);

                        recordRequest(requestLine ?? string.Empty);
                        await stream.WriteAsync(responseHeaders, 0, responseHeaders.Length);
                        await stream.WriteAsync(responseBody, 0, responseBody.Length);
                    }
                }
            };
            var proxyTask = Task.Run(() => serve(proxyListener, requestLine =>
            {
                Interlocked.Increment(ref proxyRequests);
                if (requestLine.Contains("username=") && requestLine.Contains("&password="))
                    Interlocked.Increment(ref credentialBearingProxyRequests);
            }));
            var targetTask = Task.Run(() => serve(targetListener, requestLine =>
                Interlocked.Increment(ref targetRequests)));
            var previousProxy = HttpClient.DefaultProxy;

            try
            {
                HttpClient.DefaultProxy = new WebProxy("http://127.0.0.1:" + proxyPort, false);
                var service = new StrmSyncService(null);
                var result = await service.ReconcileManagedAsync(
                    new PluginConfiguration
                    {
                        BaseUrl = "http://127.0.0.1:" + targetPort + "/",
                        Username = "managed-account",
                        Password = "managed-credential"
                    },
                    null,
                    null,
                    CancellationToken.None,
                    null);

                Assert.True(result.Compatible);
                Assert.Equal(0, Volatile.Read(ref credentialBearingProxyRequests));
                Assert.Equal(0, Volatile.Read(ref proxyRequests));
                Assert.Equal(1, Volatile.Read(ref targetRequests));
            }
            finally
            {
                HttpClient.DefaultProxy = previousProxy;
                proxyListener.Stop();
                targetListener.Stop();
                await Task.WhenAll(proxyTask, targetTask);
            }
        }

        [Fact]
        public async Task RegisterPublisherAsync_ExactContract_PostsLocalPathsWithoutCredentialsInBody()
        {
            var handler = new RequestCaptureHandler("{}");
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                await client.RegisterPublisherAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    "m3u_editor_register_publisher",
                    7,
                    new[] { "/media/Movies & Shows", "/media/TV" },
                    CancellationToken.None);

                Assert.Equal(HttpMethod.Post, handler.Method);
                Assert.Contains("action=m3u_editor_register_publisher", handler.Url);
                Assert.Contains("api_version=1", handler.Body);
                Assert.Contains("integration_id=7", handler.Body);
                Assert.Contains("writable_paths%5B0%5D=%2Fmedia%2FMovies+%26+Shows", handler.Body);
                Assert.Contains("writable_paths%5B1%5D=%2Fmedia%2FTV", handler.Body);
                Assert.DoesNotContain("account", handler.Body);
                Assert.DoesNotContain("credential", handler.Body);
                Assert.DoesNotContain("username", handler.Body);
                Assert.DoesNotContain("password", handler.Body);
            }
        }

        [Fact]
        public async Task ReportSyncResultAsync_ExactRevision_PostsRedactedResult()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("action=m3u_editor_sync_result", "{\"api_version\":1,\"data\":{\"applied\":true,\"duplicate\":false,\"mapping_uuid\":\"123e4567-e89b-12d3-a456-426614174000\",\"revision\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}}");
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var result = await client.ReportSyncResultAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    7,
                    "123e4567-e89b-12d3-a456-426614174000",
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    true,
                    "Published from https://provider.example/item using credential",
                    null,
                    CancellationToken.None);

                Assert.True(result.Applied);
                var body = Assert.Single(handler.ReceivedBodies);
                Assert.Contains("status=success", body);
                Assert.Contains("revision=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", body);
                Assert.DoesNotContain("provider.example", body);
                Assert.DoesNotContain("credential", body);
            }
        }

        [Fact]
        public async Task GetCatalogAsync_TransientServerFailure_RetriesIdempotentReadOnce()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("action=m3u_editor_catalog", "{}", System.Net.HttpStatusCode.ServiceUnavailable);
            handler.RespondWith("action=m3u_editor_catalog", CatalogJson);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var catalog = await client.GetCatalogAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None);

                Assert.Equal(Revision, catalog.Revision);
                Assert.Equal(2, handler.ReceivedUrls.Count);
            }
        }

        [Fact]
        public async Task GetCatalogAsync_StaleRevisionConflict_RetriesUntilCatalogIsPublished()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith(
                "action=m3u_editor_catalog",
                "{\"error\":{\"code\":\"stale_revision\"}}",
                System.Net.HttpStatusCode.Conflict);
            handler.RespondWith("action=m3u_editor_catalog", CatalogJson);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var catalog = await client.GetCatalogAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None);

                Assert.Equal(Revision, catalog.Revision);
                Assert.Equal(2, handler.ReceivedUrls.Count);
            }
        }

        [Fact]
        public async Task GetCatalogAsync_IncompletePublicationConflict_StopsAfterThreeAttempts()
        {
            var handler = new FakeHttpHandler();
            const string conflict = "{\"error\":{\"code\":\"incomplete_publication\"}}";
            handler.RespondWith("action=m3u_editor_catalog", conflict, System.Net.HttpStatusCode.Conflict);
            handler.RespondWith("action=m3u_editor_catalog", conflict, System.Net.HttpStatusCode.Conflict);
            handler.RespondWith("action=m3u_editor_catalog", conflict, System.Net.HttpStatusCode.Conflict);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCatalogAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None));

                Assert.Equal("Managed catalog request failed with HTTP 409.", error.Message);
                Assert.Equal(3, handler.ReceivedUrls.Count);
            }
        }

        [Fact]
        public async Task GetCatalogAsync_CancellationDuringConflictBackoff_IsHonored()
        {
            var handler = new FakeHttpHandler();
            const string conflict = "{\"error\":{\"code\":\"stale_revision\"}}";
            handler.RespondWith("action=m3u_editor_catalog", conflict, System.Net.HttpStatusCode.Conflict);
            handler.RespondWith("action=m3u_editor_catalog", conflict, System.Net.HttpStatusCode.Conflict);
            handler.RespondWith("action=m3u_editor_catalog", conflict, System.Net.HttpStatusCode.Conflict);
            using (var httpClient = new HttpClient(handler))
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
            {
                var client = new M3uEditorClient(httpClient);

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetCatalogAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    cancellation.Token));
            }
        }

        [Fact]
        public async Task GetCatalogAsync_UnrelatedConflict_FailsWithoutRetrying()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith(
                "action=m3u_editor_catalog",
                "{\"error\":{\"code\":\"mapping_not_found\"}}",
                System.Net.HttpStatusCode.Conflict);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCatalogAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None));

                Assert.Equal("Managed catalog request failed with HTTP 409.", error.Message);
                Assert.Single(handler.ReceivedUrls);
            }
        }

        [Fact]
        public async Task DiscoverCapabilityAsync_TransientServerFailure_RetriesIdempotentReadOnce()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("player_api.php?username=", "{}", System.Net.HttpStatusCode.ServiceUnavailable);
            handler.RespondWith("player_api.php?username=", CapabilityJson);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var capability = await client.DiscoverCapabilityAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None);

                Assert.NotNull(capability);
                Assert.Equal(2, handler.ReceivedUrls.Count);
            }
        }

        [Fact]
        public async Task GetCatalogAsync_UnsafeBackendUrl_FailsBeforeSendingCredentials()
        {
            var handler = new FakeHttpHandler();
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var error = await Assert.ThrowsAsync<System.InvalidOperationException>(() => client.GetCatalogAsync(
                    "file:///tmp/backend",
                    "account",
                    "credential",
                    CancellationToken.None));

                Assert.Contains("base URL", error.Message);
                Assert.Empty(handler.ReceivedUrls);
            }
        }

        [Theory]
        [InlineData("https://editor.example?next=https://other.example")]
        [InlineData("https://editor.example/#fragment")]
        [InlineData("https://account@editor.example")]
        public async Task ManagedRequests_NonConfinedOrigin_FailBeforeSendingCredentials(string baseUrl)
        {
            var handler = new FakeHttpHandler();
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCatalogAsync(
                    baseUrl, "account", "credential", CancellationToken.None));

                Assert.Contains("base URL", error.Message);
                Assert.Empty(handler.ReceivedUrls);
            }
        }

        [Theory]
        [InlineData("http://localhost:8080")]
        [InlineData("http://127.0.0.1:8080")]
        [InlineData("http://10.20.30.40:8080")]
        [InlineData("http://172.20.0.2:8080")]
        [InlineData("http://192.168.1.20:8080")]
        [InlineData("http://169.254.20.30:8080")]
        [InlineData("http://[::1]:8080")]
        [InlineData("http://[fd12:3456::1]:8080")]
        [InlineData("http://[fe80::1]:8080")]
        [InlineData("http://m3u-editor:8080")]
        public async Task ManagedRequests_TrustedHttpOrigin_IsAccepted(string baseUrl)
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("player_api.php?username=", CapabilityJson);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(
                    httpClient,
                    (host, cancellationToken) => Task.FromResult(new[] { IPAddress.Parse("172.20.0.2") }));

                var capability = await client.DiscoverCapabilityAsync(
                    baseUrl, "account", "credential", CancellationToken.None);

                Assert.NotNull(capability);
                Assert.Single(handler.ReceivedUrls);
            }
        }

        [Theory]
        [InlineData("http://8.8.8.8:8080")]
        [InlineData("http://93.184.216.34:8080")]
        [InlineData("http://editor.example:8080")]
        public async Task ManagedRequests_PublicHttpOrigin_FailsBeforeSendingCredentials(string baseUrl)
        {
            var handler = new FakeHttpHandler();
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCatalogAsync(
                    baseUrl, "account", "credential", CancellationToken.None));

                Assert.Contains("base URL", error.Message);
                Assert.DoesNotContain(baseUrl, error.ToString());
                Assert.DoesNotContain("account", error.ToString());
                Assert.DoesNotContain("credential", error.ToString());
                Assert.Empty(handler.ReceivedUrls);
            }
        }

        [Fact]
        public async Task ManagedRequests_DnsHostWithPublicAddress_FailsBeforeSendingCredentials()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("player_api.php?username=", CapabilityJson);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(
                    httpClient,
                    (host, cancellationToken) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DiscoverCapabilityAsync(
                    "http://m3u-editor:8080", "account", "credential", CancellationToken.None));

                Assert.Contains("base URL", error.Message);
                Assert.DoesNotContain("account", error.ToString());
                Assert.DoesNotContain("credential", error.ToString());
                Assert.Empty(handler.ReceivedUrls);
            }
        }

        [Fact]
        public async Task ManagedRequests_DnsHostWithMixedAddresses_FailsBeforeSendingCredentials()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("player_api.php?username=", CapabilityJson);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(
                    httpClient,
                    (host, cancellationToken) => Task.FromResult(new[]
                    {
                        IPAddress.Parse("172.20.0.2"),
                        IPAddress.Parse("93.184.216.34")
                    }));

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DiscoverCapabilityAsync(
                    "http://m3u-editor:8080", "account", "credential", CancellationToken.None));

                Assert.Contains("base URL", error.Message);
                Assert.Empty(handler.ReceivedUrls);
            }
        }

        [Fact]
        public async Task ManagedRequests_DnsHostRebinding_CannotChangeValidatedConnectionAddress()
        {
            var handler = new ConnectionCaptureHandler(CapabilityJson);
            var resolutions = 0;
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(
                    httpClient,
                    (host, cancellationToken) => Task.FromResult(new[]
                    {
                        IPAddress.Parse(Interlocked.Increment(ref resolutions) == 1
                            ? "172.20.0.2"
                            : "93.184.216.34")
                    }));

                var capability = await client.DiscoverCapabilityAsync(
                    "http://m3u-editor:8080", "account", "credential", CancellationToken.None);

                Assert.NotNull(capability);
                Assert.Equal(1, resolutions);
                Assert.Equal("172.20.0.2", handler.RequestUri.Host);
                Assert.Equal(8080, handler.RequestUri.Port);
                Assert.Equal("m3u-editor:8080", handler.Host);
            }
        }

        [Fact]
        public async Task ManagedRequests_DnsHostWithAllPrivateAddresses_IsAccepted()
        {
            var handler = new ConnectionCaptureHandler(CapabilityJson);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(
                    httpClient,
                    (host, cancellationToken) => Task.FromResult(new[]
                    {
                        IPAddress.Parse("10.20.30.40"),
                        IPAddress.Parse("fd12:3456::1")
                    }));

                var capability = await client.DiscoverCapabilityAsync(
                    "http://m3u-editor:8080", "account", "credential", CancellationToken.None);

                Assert.NotNull(capability);
                Assert.Equal("10.20.30.40", handler.RequestUri.Host);
                Assert.Equal("m3u-editor:8080", handler.Host);
            }
        }

        [Theory]
        [InlineData("https://editor.example:8443", "http://editor.example:8443/player_api.php")]
        [InlineData("http://m3u-editor:8080", "http://other-service:8080/player_api.php")]
        [InlineData("http://m3u-editor:8080", "http://m3u-editor:8081/player_api.php")]
        public async Task ManagedRequests_ResponseOriginChanged_FailsClosed(
            string baseUrl,
            string responseUrl)
        {
            using (var httpClient = new HttpClient(new ResponseOriginHandler(responseUrl, CapabilityJson)))
            {
                var client = new M3uEditorClient(
                    httpClient,
                    (host, cancellationToken) => Task.FromResult(new[] { IPAddress.Parse("172.20.0.2") }));

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DiscoverCapabilityAsync(
                    baseUrl, "account", "credential", CancellationToken.None));

                Assert.Contains("origin changed", error.Message);
                Assert.DoesNotContain(baseUrl, error.ToString());
                Assert.DoesNotContain(responseUrl, error.ToString());
            }
        }

        [Fact]
        public async Task ManagedRequests_ExactTrustedHttpResponseOrigin_IsAccepted()
        {
            using (var httpClient = new HttpClient(new ResponseOriginHandler(
                "http://172.20.0.2:8080/player_api.php", CapabilityJson)))
            {
                var client = new M3uEditorClient(
                    httpClient,
                    (host, cancellationToken) => Task.FromResult(new[] { IPAddress.Parse("172.20.0.2") }));

                var capability = await client.DiscoverCapabilityAsync(
                    "http://m3u-editor:8080", "account", "credential", CancellationToken.None);

                Assert.NotNull(capability);
            }
        }

        [Fact]
        public async Task GetCatalogAsync_OversizedDeclaredResponse_FailsClosed()
        {
            var handler = new DeclaredLengthHandler(M3uEditorClient.MaximumResponseBytes + 1L);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCatalogAsync(
                    "https://editor.example", "account", "credential", CancellationToken.None));

                Assert.Contains("response limit", error.Message);
            }
        }

        [Theory]
        [InlineData("capability", "GET")]
        [InlineData("catalog", "GET")]
        [InlineData("callback", "POST")]
        public async Task ManagedRequests_IncrementalOversizedResponse_ReadsOnlyThroughSentinel(
            string requestPath,
            string expectedMethod)
        {
            var content = new IncrementalContent(M3uEditorClient.MaximumResponseBytes + 8192);
            var handler = new IncrementalContentHandler(content);
            using (var httpClient = new HttpClient(handler))
            {
                var client = new M3uEditorClient(httpClient);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => SendManagedRequestAsync(client, requestPath));

                Assert.Contains("response limit", error.Message);
                Assert.Equal(expectedMethod, handler.Method);
                Assert.Equal(M3uEditorClient.MaximumResponseBytes + 1L, content.BytesRead);
            }
        }

        [Fact]
        public async Task GetCatalogAsync_TransportError_DoesNotExposeAuthenticatedUrl()
        {
            using (var httpClient = new HttpClient(new ThrowingHandler()))
            {
                var client = new M3uEditorClient(httpClient);

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCatalogAsync(
                    "https://editor.example",
                    "account",
                    "credential",
                    CancellationToken.None));

                Assert.DoesNotContain("account", error.ToString());
                Assert.DoesNotContain("credential", error.ToString());
                Assert.DoesNotContain("https://", error.ToString());
            }
        }

        [Fact]
        public void CatalogOutputPathAcceptance_UsesCurrentRuntimeRootSemantics()
        {
            var method = typeof(M3uEditorCatalogValidator).GetMethod(
                "IsAbsolutePath",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            Func<string, bool> accepts = path => (bool)method.Invoke(null, new object[] { path });
            var nativeAbsolutePath = Path.Combine(Path.GetTempPath(), "managed-movies");
            var candidates = new[]
            {
                nativeAbsolutePath,
                @"C:\managed\movies",
                @"\managed\movies",
                @"\\server\share\movies"
            };

            Assert.True(Path.IsPathRooted(nativeAbsolutePath));
            Assert.True(accepts(nativeAbsolutePath));
            foreach (var candidate in candidates)
            {
                if (accepts(candidate))
                {
                    Assert.True(Path.IsPathRooted(candidate), "Accepted path was not rooted on this runtime: " + candidate);
                }
            }

            if (Path.DirectorySeparatorChar == '/')
            {
                Assert.False(accepts(@"C:\managed\movies"));
                Assert.False(accepts(@"\managed\movies"));
                Assert.False(accepts(@"\\server\share\movies"));
            }
        }

        [Fact]
        public void Validate_ExcessiveMappingCount_FailsClosed()
        {
            var catalog = JsonSerializer.Deserialize<M3uEditorCatalog>(CatalogJson);
            var mapping = catalog.Mappings[0];
            catalog.Mappings = Enumerable.Repeat(mapping, M3uEditorCatalogValidator.MaximumMappings + 1).ToList();

            var error = Assert.Throws<InvalidOperationException>(() => M3uEditorCatalogValidator.Validate(catalog));

            Assert.Contains("mappings", error.Message);
        }

        [Fact]
        public void Validate_NfoPlotAtFieldLimit_Accepts()
        {
            var catalog = JsonSerializer.Deserialize<M3uEditorCatalog>(CatalogJson);
            catalog.Mappings[0].Items[0].Nfo.Plot = new string('a', 256 * 1024);

            M3uEditorCatalogValidator.Validate(catalog);
        }

        [Fact]
        public void Validate_NfoPlotOneCharacterOverFieldLimit_FailsClosed()
        {
            var catalog = JsonSerializer.Deserialize<M3uEditorCatalog>(CatalogJson);
            catalog.Mappings[0].Items[0].Nfo.Plot = new string('a', (256 * 1024) + 1);

            var error = Assert.Throws<InvalidOperationException>(() => M3uEditorCatalogValidator.Validate(catalog));

            Assert.Contains("NFO metadata", error.Message);
        }

        [Fact]
        public void Validate_PlaybackUrlOneCharacterOverFieldLimit_FailsClosed()
        {
            var catalog = JsonSerializer.Deserialize<M3uEditorCatalog>(CatalogJson);
            catalog.Mappings[0].Items[0].Variants[0].Preferred.PlaybackUrl =
                "https://editor.example/" + new string('a', 8193 - "https://editor.example/".Length);

            var error = Assert.Throws<InvalidOperationException>(() => M3uEditorCatalogValidator.Validate(catalog));

            Assert.Contains("playback URL", error.Message);
        }

        [Fact]
        public void Validate_NonManagedTargetLibrary_FailsClosed()
        {
            var catalog = JsonSerializer.Deserialize<M3uEditorCatalog>(CatalogJson);
            catalog.Mappings[0].TargetLibrary.Managed = false;

            var error = Assert.Throws<InvalidOperationException>(() => M3uEditorCatalogValidator.Validate(catalog));

            Assert.Contains("target library", error.Message);
        }

        [Fact]
        public void SafeFilename_EnforcesLengthLimit()
        {
            Assert.True(M3uEditorCatalogValidator.IsSafeFilename(new string('a', 240)));
            Assert.False(M3uEditorCatalogValidator.IsSafeFilename(new string('a', 241)));
        }

        public static IEnumerable<object[]> InvalidCatalogs
        {
            get
            {
                yield return new object[] { CatalogJson.Replace("\"api_version\": 1", "\"api_version\": 2"), "API version" };
                yield return new object[] { CatalogJson.Replace(Revision, "invalid"), "catalog revision" };
                yield return new object[] { CatalogJson.Replace("123e4567-e89b-12d3-a456-426614174000", "not-a-uuid"), "mapping identity" };
                yield return new object[] { CatalogJson.Replace("\"collection_type\": \"movies\"", "\"collection_type\": \"music\""), "collection type" };
                yield return new object[] { CatalogJson.Replace("/media/managed-movies", "relative/path"), "output path" };
                yield return new object[] { CatalogJson.Replace("/media/managed-movies", "/"), "output path" };
                yield return new object[] { CatalogJson.Replace("the-matrix-1999", "../escape"), "relative path" };
                yield return new object[] { CatalogJson.Replace("https://editor.example/play/primary", "file:///etc/passwd"), "playback URL" };
                yield return new object[] { CatalogJson.Replace("\"tmdb\": 603", "\"tmdb\": 0"), "provider ID" };
                yield return new object[] { CatalogJson.Replace("A choice.", "bad\\u0001xml"), "NFO" };
            }
        }

        private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private const string CapabilityJson = @"{
  ""user_info"": { ""auth"": 1 },
  ""m3u_editor"": {
    ""library_publishing"": {
      ""api_version"": 1,
      ""actions"": { ""register_publisher"": ""m3u_editor_register_publisher"", ""catalog"": ""m3u_editor_catalog"", ""sync_result"": ""m3u_editor_sync_result"" },
      ""snapshot_mode"": ""full"",
      ""features"": [""library_mappings"", ""variants"", ""provider_failover"", ""local_nfo"", ""revision_metadata""]
    }
  }
}";

        private const string CatalogJson = @"{
  ""api_version"": 1,
  ""full_snapshot"": true,
  ""revision"": ""aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"",
  ""mappings"": [{
    ""mapping_uuid"": ""123e4567-e89b-12d3-a456-426614174000"",
    ""integration_id"": 7,
    ""target_library"": {
      ""id"": ""library-1"",
      ""name"": ""Managed Movies"",
      ""collection_type"": ""movies"",
      ""output_path"": ""/media/managed-movies"",
      ""managed"": true
    },
    ""options"": { ""naming"": ""media-year"", ""nfo"": true, ""versions"": true, ""cleanup"": ""replace"", ""refresh"": true },
    ""full_snapshot"": true,
    ""revision"": ""bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"",
    ""items"": [{
      ""canonical_id"": ""movie:tmdb:603"",
      ""media_type"": ""movie"",
      ""display_title"": ""The Matrix"",
      ""display_title_source"": ""channel.title"",
      ""original_title"": ""The Matrix"",
      ""original_title_source"": ""display_title"",
      ""year"": 1999,
      ""ids"": { ""tmdb"": 603, ""tvdb"": null, ""imdb"": ""tt0133093"" },
      ""groups"": [""Science Fiction""],
      ""relative_folder"": ""the-matrix-1999"",
      ""base_filename"": ""the-matrix-1999"",
      ""nfo"": { ""title"": ""The Matrix"", ""original_title"": ""The Matrix"", ""year"": 1999, ""plot"": ""A choice."", ""genres"": [""Science Fiction""], ""ids"": { ""tmdb"": 603, ""tvdb"": null, ""imdb"": ""tt0133093"" } },
      ""variants"": [{
        ""key"": ""1080p-sdr-h264-aac-en-theatrical"",
        ""preferred"": { ""source_id"": 10, ""playback_url"": ""https://editor.example/play/primary"", ""playlist_id"": 2 },
        ""failover"": [{ ""source_id"": 11, ""playback_url"": ""https://editor.example/play/backup"", ""playlist_id"": 3 }],
        ""technical_metadata"": []
      }]
    }]
  }]
}";

        private static async Task SendManagedRequestAsync(M3uEditorClient client, string requestPath)
        {
            if (requestPath == "capability")
            {
                await client.DiscoverCapabilityAsync(
                    "https://editor.example", "account", "credential", CancellationToken.None);
                return;
            }

            if (requestPath == "catalog")
            {
                await client.GetCatalogAsync(
                    "https://editor.example", "account", "credential", CancellationToken.None);
                return;
            }

            await client.ReportSyncResultAsync(
                "https://editor.example",
                "account",
                "credential",
                7,
                "123e4567-e89b-12d3-a456-426614174000",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                true,
                "summary",
                null,
                CancellationToken.None);
        }

        private sealed class IncrementalContentHandler : HttpMessageHandler
        {
            private readonly HttpContent _content;

            public IncrementalContentHandler(HttpContent content)
            {
                _content = content;
            }

            public string Method { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Method = request.Method.Method;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = _content
                });
            }
        }

        private sealed class RequestCaptureHandler : HttpMessageHandler
        {
            private readonly string _responseBody;

            public RequestCaptureHandler(string responseBody)
            {
                _responseBody = responseBody;
            }

            public HttpMethod Method { get; private set; }
            public string Url { get; private set; }
            public string Body { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Method = request.Method;
                Url = request.RequestUri.ToString();
                Body = request.Content == null
                    ? string.Empty
                    : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_responseBody)
                });
            }
        }

        private sealed class ResponseOriginHandler : HttpMessageHandler
        {
            private readonly Uri _responseUri;
            private readonly string _responseBody;

            public ResponseOriginHandler(string responseUrl, string responseBody)
            {
                _responseUri = new Uri(responseUrl);
                _responseBody = responseBody;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                request.RequestUri = _responseUri;
                return Task.FromResult(CreateResponse(request));
            }

            private HttpResponseMessage CreateResponse(HttpRequestMessage request)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(_responseBody)
                };
            }
        }

        private sealed class ConnectionCaptureHandler : HttpMessageHandler
        {
            private readonly string _responseBody;

            public ConnectionCaptureHandler(string responseBody)
            {
                _responseBody = responseBody;
            }

            public Uri RequestUri { get; private set; }
            public string Host { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestUri = request.RequestUri;
                Host = request.Headers.Host;
                return Task.FromResult(CreateResponse());
            }

            private HttpResponseMessage CreateResponse()
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_responseBody)
                };
            }
        }

        private static async Task ReadRequestHeadersAsync(StreamReader reader)
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line))
                {
                    return;
                }
            }
        }

        private sealed class IncrementalContent : HttpContent
        {
            private readonly int _length;

            public IncrementalContent(int length)
            {
                _length = length;
            }

            public long BytesRead { get; private set; }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                using (var source = CreateStream())
                {
                    var buffer = new byte[8192];
                    int read;
                    while ((read = await source.ReadAsync(buffer, 0, buffer.Length)) != 0)
                    {
                        await stream.WriteAsync(buffer, 0, read);
                    }
                }
            }

            protected override Task<Stream> CreateContentReadStreamAsync()
            {
                return Task.FromResult<Stream>(CreateStream());
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }

            private Stream CreateStream()
            {
                return new IncrementalStream(_length, read => BytesRead += read);
            }
        }

        private sealed class IncrementalStream : Stream
        {
            private readonly int _length;
            private readonly Action<int> _recordRead;
            private int _position;

            public IncrementalStream(int length, Action<int> recordRead)
            {
                _length = length;
                _recordRead = recordRead;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position
            {
                get { return _position; }
                set { throw new NotSupportedException(); }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = Math.Min(count, _length - _position);
                if (read == 0)
                {
                    return 0;
                }

                Array.Fill(buffer, (byte)'x', offset, read);
                _position += read;
                _recordRead(read);
                return read;
            }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                return Task.FromException<HttpResponseMessage>(new HttpRequestException(
                    "Transport failed at https://sensitive.invalid/ for account credential"));
            }
        }

        private sealed class DeclaredLengthHandler : HttpMessageHandler
        {
            private readonly long _length;

            public DeclaredLengthHandler(long length)
            {
                _length = length;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var content = new ByteArrayContent(new byte[0]);
                content.Headers.ContentLength = _length;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = content
                });
            }
        }
    }
}
