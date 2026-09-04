using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Api;
using Emby.M3uEditor.Plugin.Service;
using Emby.M3uEditor.Plugin.Tests.Fakes;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Services;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedSetupTests : IDisposable
    {
        private readonly TempDirectory _owner = new TempDirectory();

        [Fact]
        public void SetupRoute_IsVersionedAndAdministratorAuthenticatedForGetAndPut()
        {
            var requestType = typeof(ManagedSetupRequest);
            var route = Assert.Single(requestType.GetCustomAttributes(typeof(RouteAttribute), true)
                .Cast<RouteAttribute>());
            var auth = Assert.Single(requestType.GetCustomAttributes(typeof(AuthenticatedAttribute), true)
                .Cast<AuthenticatedAttribute>());

            Assert.Equal("/M3uEditor/Managed/Setup/V1", route.Path);
            Assert.Contains("GET", route.Verbs);
            Assert.Contains("PUT", route.Verbs);
            Assert.Equal("Admin", auth.Roles);
            Assert.NotNull(typeof(M3uEditorApi).GetMethod("Get", new[] { requestType }));
            Assert.NotNull(typeof(M3uEditorApi).GetMethod("Put", new[] { requestType }));
        }

        [Fact]
        public void Setup_PositiveBinding_CreatesAndPersistsOneCanonicalWritableRoot()
        {
            var mappings = "[]";
            var config = new PluginConfiguration
            {
                ManagedMappingsJson = mappings
            };
            var saves = 0;
            var service = new ManagedSetupService(_owner.Path);

            var result = service.Put(config, 71, () => saves++);

            Assert.True(result.Ready, result.Result);
            Assert.Equal(ManagedSetupService.ApiVersion, result.CapabilityVersion);
            Assert.Equal(71, result.IntegrationId);
            Assert.Equal(Path.Join(_owner.Path, "managed-publishing"), result.ConfirmedRoot);
            Assert.True(Directory.Exists(result.ConfirmedRoot));
            Assert.Equal(result.ConfirmedRoot, config.ManagedApprovedOutputRoots);
            Assert.Equal(mappings, config.ManagedMappingsJson);
            Assert.True(config.ManagedSetupReady);
            Assert.Equal("Ready", config.ManagedSetupLastResult);
            Assert.Equal(1, saves);
        }

        [Fact]
        public void SetupRoute_SuccessfullyReplacesLiveConfigurationUsedByDashboard()
        {
            var instanceField = typeof(Plugin).GetField(
                "_instance",
                BindingFlags.NonPublic | BindingFlags.Static);
            var previousInstance = Plugin.InstanceOrNull;
            var plugin = (TestPlugin)RuntimeHelpers.GetUninitializedObject(typeof(TestPlugin));
            plugin.SetAttributes(null, _owner.Path, null);
            plugin.SetConfiguration(new PluginConfiguration { ManagedMappingsJson = "[]" });
            instanceField.SetValue(null, plugin);
            try
            {
                var result = (ManagedSetupResult)new M3uEditorApi().Put(
                    new ManagedSetupRequest { IntegrationId = 2 });
                var dashboard = M3uEditorApi.BuildManagedDashboardStatus(
                    plugin.Configuration,
                    new ManagedJobStatus { State = "idle" },
                    1,
                    10);

                Assert.True(result.Ready, result.Result);
                Assert.True(plugin.UpdateConfigurationCalled);
                Assert.True(dashboard.SetupReady);
                Assert.Equal("Ready", dashboard.SetupResult);
                Assert.True(dashboard.ConfigurationValid);
                Assert.Equal(2, dashboard.IntegrationId);
            }
            finally
            {
                instanceField.SetValue(null, previousInstance);
            }
        }

        [Fact]
        public void SetupRoute_SuccessClearsPriorValidationErrorFromLiveDashboardState()
        {
            var instanceField = typeof(Plugin).GetField(
                "_instance",
                BindingFlags.NonPublic | BindingFlags.Static);
            var previousInstance = Plugin.InstanceOrNull;
            var plugin = (TestPlugin)RuntimeHelpers.GetUninitializedObject(typeof(TestPlugin));
            plugin.SetAttributes(null, _owner.Path, null);
            plugin.SetConfiguration(new PluginConfiguration
            {
                ManagedMappingsJson = "[]",
                ManagedLastError = "The managed backend base URL must be a confined HTTPS origin."
            });
            instanceField.SetValue(null, plugin);
            try
            {
                var result = (ManagedSetupResult)new M3uEditorApi().Put(
                    new ManagedSetupRequest { IntegrationId = 2 });
                var dashboard = M3uEditorApi.BuildManagedDashboardStatus(
                    plugin.Configuration,
                    new ManagedJobStatus { State = "idle" },
                    1,
                    10);

                Assert.True(result.Ready, result.Result);
                Assert.True(dashboard.SetupReady);
                Assert.Equal("Ready", dashboard.SetupResult);
                Assert.True(dashboard.ConfigurationValid);
                Assert.Equal(2, dashboard.IntegrationId);
                Assert.Equal(string.Empty, dashboard.LastError);
            }
            finally
            {
                instanceField.SetValue(null, previousInstance);
            }
        }

        [Fact]
        public async Task SetupRoute_ConfigurationTransactionDoesNotOverwriteConcurrentReplacement()
        {
            var instanceField = typeof(Plugin).GetField(
                "_instance",
                BindingFlags.NonPublic | BindingFlags.Static);
            var previousInstance = Plugin.InstanceOrNull;
            var plugin = (TestPlugin)RuntimeHelpers.GetUninitializedObject(typeof(TestPlugin));
            var setupCommitEntered = new ManualResetEventSlim(false);
            var allowSetupCommit = new ManualResetEventSlim(false);
            var updateAttempted = new ManualResetEventSlim(false);
            plugin.SetAttributes(null, _owner.Path, null);
            plugin.SetConfiguration(new PluginConfiguration { ManagedMappingsJson = "[]" });
            plugin.BeforeUpdateConfiguration = () =>
            {
                setupCommitEntered.Set();
                allowSetupCommit.Wait();
            };
            plugin.UpdateConfigurationAttempted = () => updateAttempted.Set();
            instanceField.SetValue(null, plugin);
            try
            {
                var setup = Task.Run(() => (ManagedSetupResult)new M3uEditorApi().Put(
                    new ManagedSetupRequest { IntegrationId = 2 }));
                Assert.True(await Task.Run(() => setupCommitEntered.Wait(TimeSpan.FromSeconds(5))));
                updateAttempted.Reset();

                var replacement = new PluginConfiguration
                {
                    HttpUserAgent = "unrelated-administrator-replacement"
                };
                var update = Task.Run(() => plugin.UpdateConfiguration(replacement));
                Assert.True(await Task.Run(() => updateAttempted.Wait(TimeSpan.FromSeconds(5))));
                var completion = await Task.WhenAny(update, Task.Delay(TimeSpan.FromMilliseconds(100)));
                Assert.NotSame(update, completion);

                allowSetupCommit.Set();
                var result = await setup;
                await update;

                Assert.True(result.Ready, result.Result);
                Assert.Same(replacement, plugin.Configuration);
                Assert.Equal("unrelated-administrator-replacement", plugin.Configuration.HttpUserAgent);
            }
            finally
            {
                allowSetupCommit.Set();
                instanceField.SetValue(null, previousInstance);
                setupCommitEntered.Dispose();
                allowSetupCommit.Dispose();
                updateAttempted.Dispose();
            }
        }

        [Fact]
        public void Setup_UnreferencedDisjointRoot_IsNotRetained()
        {
            using (var legacy = new TempDirectory())
            {
                var config = new PluginConfiguration
                {
                    ManagedApprovedOutputRoots = legacy.Path,
                    ManagedMappingsJson = "[]"
                };

                var result = new ManagedSetupService(_owner.Path).Put(config, 71, () => { });

                Assert.True(result.Ready, result.Result);
                Assert.Equal(result.ConfirmedRoot, config.ManagedApprovedOutputRoots);
                Assert.False(ManagedOutputPolicy.IsApproved(
                    Path.Join(legacy.Path, "movies"),
                    config.ManagedApprovedOutputRoots,
                    out _));
                Assert.Equal("[]", config.ManagedMappingsJson);
            }
        }

        [Fact]
        public void Setup_MappingRemoved_PrunesLegacyRootOnNextRequest()
        {
            using (var legacy = new TempDirectory())
            {
                var config = new PluginConfiguration
                {
                    ManagedApprovedOutputRoots = legacy.Path,
                    ManagedMappingsJson = JsonSerializer.Serialize(new List<ManagedMappingState>
                    {
                        new ManagedMappingState { OutputPath = Path.Join(legacy.Path, "movies") }
                    })
                };
                var saves = 0;
                var service = new ManagedSetupService(_owner.Path);

                var first = service.Put(config, 71, () => saves++);
                config.ManagedMappingsJson = "[]";
                var second = service.Put(config, 71, () => saves++);

                Assert.True(first.Ready, first.Result);
                Assert.True(second.Ready, second.Result);
                Assert.Equal(second.ConfirmedRoot, config.ManagedApprovedOutputRoots);
                Assert.False(ManagedOutputPolicy.IsApproved(
                    Path.Join(legacy.Path, "movies"),
                    config.ManagedApprovedOutputRoots,
                    out _));
                Assert.Equal("[]", config.ManagedMappingsJson);
                Assert.Equal(2, saves);
            }
        }

        [Fact]
        public void Setup_MalformedMappingState_LeavesAllConfigurationUnchanged()
        {
            using (var legacy = new TempDirectory())
            {
                var config = new PluginConfiguration
                {
                    ManagedPublishingIntegrationId = 71,
                    ManagedPublishingApiVersion = ManagedSetupService.ApiVersion,
                    ManagedApprovedOutputRoots = legacy.Path,
                    ManagedMappingsJson = "not-json",
                    ManagedSetupReady = true,
                    ManagedSetupLastResult = "Previous result"
                };
                var saves = 0;

                var result = new ManagedSetupService(_owner.Path).Put(config, 71, () => saves++);

                Assert.False(result.Ready);
                Assert.Null(result.ConfirmedRoot);
                Assert.DoesNotContain(legacy.Path, result.Result);
                Assert.Equal(71, config.ManagedPublishingIntegrationId);
                Assert.Equal(ManagedSetupService.ApiVersion, config.ManagedPublishingApiVersion);
                Assert.Equal(legacy.Path, config.ManagedApprovedOutputRoots);
                Assert.Equal("not-json", config.ManagedMappingsJson);
                Assert.True(config.ManagedSetupReady);
                Assert.Equal("Previous result", config.ManagedSetupLastResult);
                Assert.Equal(0, saves);
            }
        }

        [Fact]
        public void Setup_EmptyApprovedRootsWithMalformedMapping_RejectsBeforeMutation()
        {
            var config = new PluginConfiguration
            {
                ManagedPublishingIntegrationId = 71,
                ManagedPublishingApiVersion = ManagedSetupService.ApiVersion,
                ManagedMappingsJson = "not-json",
                ManagedSetupReady = true,
                ManagedSetupLastResult = "Previous result"
            };
            var originalConfiguration = JsonSerializer.Serialize(config);
            var candidate = Path.Join(_owner.Path, "managed-publishing");
            var saves = 0;

            var result = new ManagedSetupService(_owner.Path).Put(config, 71, () => saves++);

            Assert.False(result.Ready);
            Assert.Null(result.ConfirmedRoot);
            Assert.DoesNotContain("not-json", result.Result);
            Assert.False(Directory.Exists(candidate));
            Assert.Equal(0, saves);
            Assert.Equal(originalConfiguration, JsonSerializer.Serialize(config));
        }

        [Fact]
        public void Setup_EmptyApprovedRootsWithOutsideMapping_RejectsBeforeMutation()
        {
            using (var outside = new TempDirectory())
            {
                var outsidePath = Path.Join(outside.Path, "movies");
                var config = new PluginConfiguration
                {
                    ManagedPublishingIntegrationId = 71,
                    ManagedPublishingApiVersion = ManagedSetupService.ApiVersion,
                    ManagedMappingsJson = JsonSerializer.Serialize(new List<ManagedMappingState>
                    {
                        new ManagedMappingState { OutputPath = outsidePath }
                    }),
                    ManagedSetupReady = true,
                    ManagedSetupLastResult = "Previous result"
                };
                var originalConfiguration = JsonSerializer.Serialize(config);
                var candidate = Path.Join(_owner.Path, "managed-publishing");
                var saves = 0;

                var result = new ManagedSetupService(_owner.Path).Put(config, 71, () => saves++);

                Assert.False(result.Ready);
                Assert.Null(result.ConfirmedRoot);
                Assert.DoesNotContain(outsidePath, result.Result);
                Assert.False(Directory.Exists(candidate));
                Assert.Equal(0, saves);
                Assert.Equal(originalConfiguration, JsonSerializer.Serialize(config));
            }
        }

        [Fact]
        public void Setup_MappingWithoutProvableOutput_LeavesAllConfigurationUnchanged()
        {
            using (var legacy = new TempDirectory())
            {
                var mappings = JsonSerializer.Serialize(new List<ManagedMappingState>
                {
                    new ManagedMappingState { MappingUuid = "existing" }
                });
                var config = new PluginConfiguration
                {
                    ManagedPublishingIntegrationId = 71,
                    ManagedApprovedOutputRoots = legacy.Path,
                    ManagedMappingsJson = mappings
                };
                var saves = 0;

                var result = new ManagedSetupService(_owner.Path).Put(config, 71, () => saves++);

                Assert.False(result.Ready);
                Assert.Null(result.ConfirmedRoot);
                Assert.DoesNotContain(legacy.Path, result.Result);
                Assert.Equal(71, config.ManagedPublishingIntegrationId);
                Assert.Equal(legacy.Path, config.ManagedApprovedOutputRoots);
                Assert.Equal(mappings, config.ManagedMappingsJson);
                Assert.False(config.ManagedSetupReady);
                Assert.Equal(0, saves);
            }
        }

        [Fact]
        public void Setup_ExistingMappedLegacyRoot_RemainsApprovedWithoutExposure()
        {
            using (var legacy = new TempDirectory())
            {
                var legacyOutput = Path.Join(legacy.Path, "movies");
                var mappings = JsonSerializer.Serialize(new List<ManagedMappingState>
                {
                    new ManagedMappingState { OutputPath = legacyOutput }
                });
                var config = new PluginConfiguration
                {
                    ManagedPublishingIntegrationId = 71,
                    ManagedApprovedOutputRoots = legacy.Path,
                    ManagedMappingsJson = mappings
                };
                var service = new ManagedSetupService(_owner.Path);
                var saves = 0;

                var result = service.Put(config, 71, () => saves++);
                var candidate = Path.Join(_owner.Path, "managed-publishing");
                var approvedRoots = config.ManagedApprovedOutputRoots;
                var repeated = service.Put(config, 71, () => saves++);

                Assert.True(result.Ready, result.Result);
                Assert.True(repeated.Ready, repeated.Result);
                Assert.Equal(candidate, result.ConfirmedRoot);
                Assert.Equal(candidate, repeated.ConfirmedRoot);
                Assert.True(ManagedOutputPolicy.IsApproved(
                    legacyOutput,
                    config.ManagedApprovedOutputRoots,
                    out var legacyError), legacyError);
                Assert.True(ManagedOutputPolicy.IsApproved(
                    Path.Join(candidate, "series"),
                    config.ManagedApprovedOutputRoots,
                    out var candidateError), candidateError);
                Assert.Equal(mappings, config.ManagedMappingsJson);
                Assert.Equal(71, config.ManagedPublishingIntegrationId);
                Assert.Equal(approvedRoots, config.ManagedApprovedOutputRoots);
                Assert.Equal(1, saves);
            }
        }

        [Fact]
        public void Setup_RepeatedRequest_IsIdempotent()
        {
            var config = new PluginConfiguration();
            var saves = 0;
            var service = new ManagedSetupService(_owner.Path);

            var first = service.Put(config, 17, () => saves++);
            var second = service.Put(config, 17, () => saves++);

            Assert.True(first.Ready);
            Assert.True(second.Ready);
            Assert.Equal(first.ConfirmedRoot, second.ConfirmedRoot);
            Assert.Equal(1, saves);
        }

        [Fact]
        public void Setup_IdempotentRequestWithStaleError_PersistsClearAndRestoresOnSaveFailure()
        {
            const string staleError = "The managed backend base URL must be a confined HTTPS origin.";
            var config = new PluginConfiguration();
            var service = new ManagedSetupService(_owner.Path);

            var initial = service.Put(config, 17, () => { });
            config.ManagedLastError = staleError;
            var successful = service.Put(config, 17, () =>
            {
                Assert.Equal(string.Empty, config.ManagedLastError);
            });

            Assert.True(initial.Ready, initial.Result);
            Assert.True(successful.Ready, successful.Result);
            Assert.Equal(string.Empty, config.ManagedLastError);

            config.ManagedLastError = staleError;
            var failed = service.Put(config, 17, () =>
            {
                Assert.Equal(string.Empty, config.ManagedLastError);
                throw new IOException("save failed");
            });

            Assert.False(failed.Ready);
            Assert.Equal(staleError, config.ManagedLastError);
        }

        [Fact]
        public async Task Setup_ConcurrentConflictingBindings_OnlyOneCommits()
        {
            var config = new PluginConfiguration();
            var serviceA = new ManagedSetupService(_owner.Path);
            var serviceB = new ManagedSetupService(_owner.Path);

            var results = await Task.WhenAll(
                Task.Run(() => serviceA.Put(config, 41, () => { })),
                Task.Run(() => serviceB.Put(config, 42, () => { })));

            var accepted = Assert.Single(results.Where(result => result.Ready));
            var rejected = Assert.Single(results.Where(result => !result.Ready));
            Assert.Equal(accepted.IntegrationId, config.ManagedPublishingIntegrationId);
            Assert.Equal(0, rejected.IntegrationId);
            Assert.Contains("conflict", rejected.Result, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(string.Empty, config.ManagedMappingsJson);
        }

        [Fact]
        public void Setup_OverlappingExistingApproval_LeavesConfigurationUnchanged()
        {
            var config = new PluginConfiguration
            {
                ManagedApprovedOutputRoots = _owner.Path,
                ManagedMappingsJson = "existing mappings"
            };

            var result = new ManagedSetupService(_owner.Path).Put(config, 3, () => { });

            Assert.False(result.Ready);
            Assert.Contains("overlap", result.Result, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(_owner.Path, config.ManagedApprovedOutputRoots);
            Assert.Equal("existing mappings", config.ManagedMappingsJson);
        }

        [Fact]
        public void Setup_OverlappingLegacyApprovals_LeavesConfigurationUnchanged()
        {
            using (var legacy = new TempDirectory())
            {
                var nested = Path.Join(legacy.Path, "nested");
                var roots = legacy.Path + Environment.NewLine + nested;
                var config = new PluginConfiguration
                {
                    ManagedApprovedOutputRoots = roots,
                    ManagedMappingsJson = "[]"
                };

                var result = new ManagedSetupService(_owner.Path).Put(config, 3, () => { });

                Assert.False(result.Ready);
                Assert.Contains("overlap", result.Result, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(roots, config.ManagedApprovedOutputRoots);
                Assert.Equal("[]", config.ManagedMappingsJson);
            }
        }

        [Fact]
        public void Setup_FileSystemLegacyApproval_IsRejectedWithoutExposingPath()
        {
            var root = Path.GetPathRoot(_owner.Path);
            var config = new PluginConfiguration
            {
                ManagedApprovedOutputRoots = root,
                ManagedMappingsJson = "[]"
            };

            var result = new ManagedSetupService(_owner.Path).Put(config, 3, () => { });

            Assert.False(result.Ready);
            Assert.Null(result.ConfirmedRoot);
            Assert.DoesNotContain(root, result.Result);
            Assert.Equal(root, config.ManagedApprovedOutputRoots);
            Assert.Equal("[]", config.ManagedMappingsJson);
        }

        [Fact]
        public void Setup_FileSystemOwnerRoot_IsRejectedWithoutExposingPath()
        {
            var ownerRoot = Path.GetPathRoot(_owner.Path);
            var config = new PluginConfiguration();

            var result = new ManagedSetupService(ownerRoot).Put(config, 3, () => { });

            Assert.False(result.Ready);
            Assert.Null(result.ConfirmedRoot);
            Assert.DoesNotContain(ownerRoot, result.Result);
            Assert.Equal(0, config.ManagedPublishingIntegrationId);
            Assert.Equal(string.Empty, config.ManagedApprovedOutputRoots);
        }

        [Fact]
        public void Setup_ReparseTraversal_IsRejectedWithoutChangingConfiguration()
        {
            if (Path.DirectorySeparatorChar != '/')
            {
                return;
            }

            using (var outside = new TempDirectory())
            {
                var link = Path.Join(_owner.Path, "linked-owner");
                Directory.CreateSymbolicLink(link, outside.Path);
                var config = new PluginConfiguration();

                var result = new ManagedSetupService(link).Put(config, 3, () => { });

                Assert.False(result.Ready);
                Assert.Contains("safe", result.Result, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(0, config.ManagedPublishingIntegrationId);
                Assert.Equal(string.Empty, config.ManagedApprovedOutputRoots);
            }
        }

        [Fact]
        public void Setup_ReparseLegacyApproval_IsRejectedWithoutChangingConfiguration()
        {
            if (Path.DirectorySeparatorChar != '/')
            {
                return;
            }

            using (var outside = new TempDirectory())
            using (var legacyParent = new TempDirectory())
            {
                var link = Path.Join(legacyParent.Path, "legacy-link");
                Directory.CreateSymbolicLink(link, outside.Path);
                var config = new PluginConfiguration
                {
                    ManagedApprovedOutputRoots = link,
                    ManagedMappingsJson = "[]"
                };

                var result = new ManagedSetupService(_owner.Path).Put(config, 3, () => { });

                Assert.False(result.Ready);
                Assert.Null(result.ConfirmedRoot);
                Assert.DoesNotContain(link, result.Result);
                Assert.Equal(link, config.ManagedApprovedOutputRoots);
                Assert.Equal("[]", config.ManagedMappingsJson);
            }
        }

        [Fact]
        public void Get_BoundedCompatibilityRoots_ReturnsOnlyCandidate()
        {
            using (var legacy = new TempDirectory())
            {
                var config = new PluginConfiguration
                {
                    ManagedApprovedOutputRoots = legacy.Path,
                    ManagedMappingsJson = JsonSerializer.Serialize(new List<ManagedMappingState>
                    {
                        new ManagedMappingState { OutputPath = Path.Join(legacy.Path, "movies") }
                    })
                };
                var service = new ManagedSetupService(_owner.Path);
                var setup = service.Put(config, 71, () => { });

                var result = service.Get(config);

                Assert.True(setup.Ready, setup.Result);
                Assert.True(result.Ready, result.Result);
                Assert.Equal(setup.ConfirmedRoot, result.ConfirmedRoot);
                Assert.DoesNotContain(legacy.Path, result.ConfirmedRoot);
                Assert.DoesNotContain(legacy.Path, result.Result);
            }
        }

        [Fact]
        public void Setup_SaveFailure_RollsBackCompleteRootSetAndSetupState()
        {
            var originalRoot = Path.Join(_owner.Path, "old-root");
            var mappings = JsonSerializer.Serialize(new List<ManagedMappingState>
            {
                new ManagedMappingState { OutputPath = Path.Join(originalRoot, "movies") }
            });
            var config = new PluginConfiguration
            {
                ManagedPublishingIntegrationId = 8,
                ManagedApprovedOutputRoots = originalRoot,
                ManagedMappingsJson = mappings,
                ManagedLastError = "The managed backend base URL must be a confined HTTPS origin."
            };

            var result = new ManagedSetupService(_owner.Path).Put(
                config,
                8,
                () =>
                {
                    Assert.True(ManagedOutputPolicy.IsApproved(
                        Path.Join(originalRoot, "movies"),
                        config.ManagedApprovedOutputRoots,
                        out var legacyError), legacyError);
                    Assert.True(ManagedOutputPolicy.IsApproved(
                        Path.Join(_owner.Path, "managed-publishing", "series"),
                        config.ManagedApprovedOutputRoots,
                        out var candidateError), candidateError);
                    Assert.Equal(string.Empty, config.ManagedLastError);
                    throw new IOException("secret /path/to/config");
                });

            Assert.False(result.Ready);
            Assert.Equal(0, result.IntegrationId);
            Assert.Null(result.ConfirmedRoot);
            Assert.DoesNotContain("secret", result.Result);
            Assert.DoesNotContain("/path", result.Result);
            Assert.Equal(8, config.ManagedPublishingIntegrationId);
            Assert.Equal(originalRoot, config.ManagedApprovedOutputRoots);
            Assert.Equal(mappings, config.ManagedMappingsJson);
            Assert.False(config.ManagedSetupReady);
            Assert.Equal("The managed backend base URL must be a confined HTTPS origin.", config.ManagedLastError);
        }

        [Fact]
        public void Setup_UnexpectedSaveFailure_RestoresStateAndPropagates()
        {
            var config = new PluginConfiguration
            {
                ManagedPublishingIntegrationId = 8,
                ManagedApprovedOutputRoots = Path.Join(_owner.Path, "old-root"),
                ManagedSetupLastResult = "Previous result",
                ManagedPublishingApiVersion = 7
            };
            var originalConfiguration = JsonSerializer.Serialize(config);

            Assert.Throws<ApplicationException>(() => new ManagedSetupService(_owner.Path).Put(
                config,
                8,
                () => throw new ApplicationException("unexpected failure")));

            Assert.Equal(originalConfiguration, JsonSerializer.Serialize(config));
        }

        public void Dispose()
        {
            _owner.Dispose();
        }

        private sealed class TestPlugin : Plugin
        {
            public bool UpdateConfigurationCalled { get; private set; }

            public Action BeforeUpdateConfiguration { get; set; }

            public Action UpdateConfigurationAttempted { get; set; }

            private TestPlugin()
                : base(null, null, null, null)
            {
            }

            public void SetConfiguration(PluginConfiguration configuration)
            {
                Configuration = configuration;
            }

            public override void UpdateConfiguration(BasePluginConfiguration configuration)
            {
                UpdateConfigurationAttempted?.Invoke();
                lock (ConfigurationTransactionGate)
                {
                    UpdateConfigurationCalled = true;
                    BeforeUpdateConfiguration?.Invoke();
                    Configuration = (PluginConfiguration)configuration;
                }
            }

            public override void SaveConfiguration()
            {
            }
        }
    }
}
