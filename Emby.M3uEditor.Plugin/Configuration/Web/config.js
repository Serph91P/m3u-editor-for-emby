define(['baseView', 'loading', 'emby-input', 'emby-select', 'emby-checkbox', 'emby-button'],
function (BaseView, loading) {
    'use strict';

    var pluginId = 'b7e3c4a1-9f2d-4e8b-a5c6-d1f0e2b3c4a5';
    var managedActionPollId = null;
    var managedPage = 1;

    function View(view, params) {
        BaseView.apply(this, arguments);

        this.loadedCategories = [];
        this.selectedCategoryIds = [];

        var self = this;
        var form = view.querySelector('.m3uEditorConfigForm');

        form.addEventListener('submit', function (event) {
            event.preventDefault();
            saveConfig(self);
        });

        view.querySelector('.txtBaseUrl').addEventListener('input', function () {
            updateUrlSecurityWarning(view);
        });
        view.querySelector('.btnTestConnection').addEventListener('click', function () {
            testConnection(self);
        });
        view.querySelector('.btnCheckProbeCoverage').addEventListener('click', function () {
            checkProbeDataCoverage(view);
        });
        view.querySelector('.selectEpgSource').addEventListener('change', function () {
            updateEpgVisibility(view);
        });
        view.querySelector('.chkEnableChannelNameCleaning').addEventListener('change', function () {
            updateChannelNameCleaningVisibility(view);
        });
        view.querySelector('.btnLoadCategories').addEventListener('click', function () {
            loadCategories(self);
        });
        view.querySelector('.btnSelectAllCategories').addEventListener('click', function () {
            toggleAllCategories(view, true);
        });
        view.querySelector('.btnDeselectAllCategories').addEventListener('click', function () {
            toggleAllCategories(view, false);
        });
        view.querySelector('.btnRefreshCache').addEventListener('click', function () {
            refreshCache(view);
        });
        view.querySelector('.btnRefreshChannelIcons').addEventListener('click', function () {
            refreshChannelIcons(view);
        });
        view.querySelector('.categoriesContainer').addEventListener('change', function (event) {
            if (event.target.classList.contains('categoryCheckbox')) {
                updateCategoryCountBadge(view);
            }
        });

        view.querySelector('.btnManagedReconcile').addEventListener('click', function () {
            reconcileManaged(view);
        });
        view.querySelector('.btnManagedRollback').addEventListener('click', function () {
            rollbackManaged(view);
        });
        view.querySelector('.btnManagedPreviousPage').addEventListener('click', function () {
            managedPage = Math.max(1, managedPage - 1);
            loadDashboard(view);
        });
        view.querySelector('.btnManagedNextPage').addEventListener('click', function () {
            managedPage++;
            loadDashboard(view);
        });
        view.querySelector('.btnOpenManagedPublishing').addEventListener('click', function () {
            switchTab(view, 'managedPublishing');
        });
        view.querySelector('.btnGoToSettings').addEventListener('click', function () {
            switchTab(view, 'generic');
        });

        view.querySelector('.btnDownloadLog').addEventListener('click', function () {
            window.open(ApiClient.getUrl('M3uEditor/Logs') + '?api_key=' + ApiClient.accessToken(), '_blank');
        });
        view.querySelector('.updateBannerDismiss').addEventListener('click', function () {
            view.querySelector('.updateBanner').style.display = 'none';
        });
        view.querySelector('.chkUseBetaChannel').addEventListener('change', function () {
            saveConfig(self, function () { checkForUpdate(view); });
        });
        view.querySelector('.btnInstallUpdate').addEventListener('click', function () {
            installUpdate(view);
        });
        view.querySelector('.btnRestartEmby').addEventListener('click', function () {
            restartEmby(view);
        });

        setupCategorySearch(view);
        initSettingsCollapsibles(view);

        var tabButtons = view.querySelectorAll('.tabBtn');
        for (var i = 0; i < tabButtons.length; i++) {
            tabButtons[i].addEventListener('click', function () {
                var tab = this.getAttribute('data-tab');
                switchTab(view, tab);
                if (tab === 'dashboard' || tab === 'managedPublishing') {
                    loadDashboard(view);
                } else if (tab === 'liveTv' && self.loadedCategories.length === 0) {
                    loadCategories(self);
                }
            });
        }

        switchTab(view, 'dashboard');
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function () {
        BaseView.prototype.onResume.apply(this, arguments);
        var self = this;
        var followUp = function () {
            loadDashboard(self.view);
            checkForUpdate(self.view);
        };
        loadConfig(this).then(followUp, followUp);
    };

    View.prototype.onPause = function () {};

    function loadConfig(instance) {
        loading.show();
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            var view = instance.view;

            view.querySelector('.txtBaseUrl').value = config.BaseUrl || '';
            view.querySelector('.txtUsername').value = config.Username || '';
            view.querySelector('.txtPassword').value = config.Password || '';
            view.querySelector('.txtHttpUserAgent').value = config.HttpUserAgent || '';
            view.querySelector('.chkEnableDiagnosticsLogging').checked = !!config.EnableDiagnosticsLogging || !!config.EnableLiveTvDiagnostics;
            view.querySelector('.chkEnableLiveTv').checked = config.EnableLiveTv !== false;
            view.querySelector('.selOutputFormat').value = config.LiveTvOutputFormat || 'ts';
            view.querySelector('.chkIncludeAdult').checked = !!config.IncludeAdultChannels;
            view.querySelector('.chkUseM3uLogoForAllChannelImages').checked = !!config.UseM3uLogoForAllChannelImages;
            view.querySelector('.chkEnableChannelNameCleaning').checked = !!config.EnableChannelNameCleaning;

            var epgNameToInt = { XtreamServer: '0', CustomUrl: '1', Disabled: '2' };
            var epgValue = config.EpgSource;
            view.querySelector('.selectEpgSource').value = epgNameToInt[epgValue] || (epgValue || 0).toString();
            view.querySelector('.txtCustomEpgUrl').value = config.CustomEpgUrl || '';
            view.querySelector('.txtEpgCacheMinutes').value = config.EpgCacheMinutes || 30;
            view.querySelector('.txtEpgDaysToFetch').value = config.EpgDaysToFetch || 2;
            view.querySelector('.txtM3UCacheMinutes').value = config.M3UCacheMinutes || 15;

            var terms = config.ChannelRemoveTerms || '';
            view.querySelector('.txtChannelRemoveTerms').value = terms.split(',').map(function (term) {
                return term.trim();
            }).filter(Boolean).join('\n');

            instance.selectedCategoryIds = config.SelectedLiveCategoryIds || [];
            view.querySelector('.chkUseBetaChannel').checked = !!config.UseBetaChannel;

            updateUrlSecurityWarning(view);
            updateEpgVisibility(view);
            updateChannelNameCleaningVisibility(view);
            renderHealthBar(view, config);
            updateDashboardEmptyState(view, config);
            loadCachedCategories(instance, config);
            loading.hide();
        }).catch(function (error) {
            loading.hide();
            console.error('M3uEditor: failed to load plugin configuration', error);
            renderHealthBar(instance.view, {});
            updateDashboardEmptyState(instance.view, {});
            throw error;
        });
    }

    function saveConfig(instance, callback) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            var view = instance.view;

            config.BaseUrl = view.querySelector('.txtBaseUrl').value.replace(/\/+$/, '');
            config.Username = view.querySelector('.txtUsername').value;
            config.Password = view.querySelector('.txtPassword').value;
            config.HttpUserAgent = view.querySelector('.txtHttpUserAgent').value;
            config.EnableDiagnosticsLogging = view.querySelector('.chkEnableDiagnosticsLogging').checked;
            config.EnableLiveTvDiagnostics = config.EnableDiagnosticsLogging;

            config.EnableLiveTv = view.querySelector('.chkEnableLiveTv').checked;
            config.LiveTvOutputFormat = view.querySelector('.selOutputFormat').value;
            config.IncludeAdultChannels = view.querySelector('.chkIncludeAdult').checked;
            config.UseM3uLogoForAllChannelImages = view.querySelector('.chkUseM3uLogoForAllChannelImages').checked;
            config.EnableChannelNameCleaning = view.querySelector('.chkEnableChannelNameCleaning').checked;
            config.ChannelRemoveTerms = view.querySelector('.txtChannelRemoveTerms').value.split('\n').map(function (term) {
                return term.trim();
            }).filter(Boolean).join(',');

            config.EpgSource = parseInt(view.querySelector('.selectEpgSource').value, 10);
            config.CustomEpgUrl = view.querySelector('.txtCustomEpgUrl').value.trim();
            config.EpgCacheMinutes = parseInt(view.querySelector('.txtEpgCacheMinutes').value, 10) || 30;
            config.EpgDaysToFetch = parseInt(view.querySelector('.txtEpgDaysToFetch').value, 10) || 2;
            config.M3UCacheMinutes = parseInt(view.querySelector('.txtM3UCacheMinutes').value, 10) || 15;
            config.SelectedLiveCategoryIds = getSelectedCategoryIds(instance);
            config.UseBetaChannel = view.querySelector('.chkUseBetaChannel').checked;

            ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();
                if (typeof callback === 'function') callback();
            }, function (error) {
                loading.hide();
                Dashboard.alert('Failed to save configuration.' + formatError(error));
            });
        }, function (error) {
            loading.hide();
            Dashboard.alert('Failed to load current configuration before saving.' + formatError(error));
        });
    }

    function formatError(error) {
        var detail = error && (error.statusText || error.message);
        return detail ? ' (' + detail + ')' : '';
    }

    function switchTab(view, tabName) {
        var panelMap = {
            dashboard: '.tabDashboard',
            managedPublishing: '.tabManagedPublishing',
            generic: '.tabGeneric',
            liveTv: '.tabLiveTv'
        };
        var buttonMap = {
            dashboard: '.tabBtnDashboard',
            managedPublishing: '.tabBtnManagedPublishing',
            generic: '.tabBtnGeneric',
            liveTv: '.tabBtnLiveTv'
        };
        var panels = view.querySelectorAll('.tabPanel');
        var buttons = view.querySelectorAll('.tabBtn');
        var i;

        for (i = 0; i < panels.length; i++) panels[i].style.display = 'none';
        for (i = 0; i < buttons.length; i++) {
            buttons[i].style.opacity = '0.7';
            buttons[i].style.borderBottomColor = 'transparent';
        }

        var panel = view.querySelector(panelMap[tabName]);
        var button = view.querySelector(buttonMap[tabName]);
        if (panel) panel.style.display = 'block';
        if (button) {
            button.style.opacity = '1';
            button.style.borderBottomColor = '#52B54B';
        }

        view.querySelector('.stickyFooter').style.display =
            tabName === 'dashboard' || tabName === 'managedPublishing' ? 'none' : '';
    }

    function updateEpgVisibility(view) {
        var source = parseInt(view.querySelector('.selectEpgSource').value, 10);
        view.querySelector('.epgSettings').style.display = source !== 2 ? '' : 'none';
        view.querySelector('.epgCustomUrlSettings').style.display = source === 1 ? '' : 'none';
    }

    function updateChannelNameCleaningVisibility(view) {
        var enabled = view.querySelector('.chkEnableChannelNameCleaning').checked;
        view.querySelector('.channelNameCleaningSettings').style.display = enabled ? '' : 'none';
    }

    function updateUrlSecurityWarning(view) {
        var url = (view.querySelector('.txtBaseUrl').value || '').trim();
        view.querySelector('.xtreamInsecureWarning').style.display = shouldWarnAboutHttpCredentials(url) ? '' : 'none';
    }

    function shouldWarnAboutHttpCredentials(url) {
        if (!url || url.toLowerCase().indexOf('http://') !== 0) return false;

        var host;
        try {
            host = new URL(url).hostname.toLowerCase();
        } catch (error) {
            return true;
        }
        if (host === 'localhost' || host === '127.0.0.1' || host === '::1') return false;
        if (!/^\d{1,3}(\.\d{1,3}){3}$/.test(host)) return true;

        var parts = host.split('.').map(function (part) { return parseInt(part, 10); });
        if (parts.some(function (part) { return isNaN(part) || part < 0 || part > 255; })) return true;
        return !(parts[0] === 10 ||
            (parts[0] === 172 && parts[1] >= 16 && parts[1] <= 31) ||
            (parts[0] === 192 && parts[1] === 168));
    }

    function initSettingsCollapsibles(view) {
        var detailsList = view.querySelectorAll('.tabPanel details[data-collapse-key]');
        for (var i = 0; i < detailsList.length; i++) {
            (function (details) {
                var storageKey = 'm3uEditor.' + details.getAttribute('data-collapse-key');
                try {
                    var saved = window.localStorage.getItem(storageKey);
                    if (saved === 'open') details.open = true;
                    if (saved === 'closed') details.open = false;
                } catch (error) {}
                details.addEventListener('toggle', function () {
                    try {
                        window.localStorage.setItem(storageKey, details.open ? 'open' : 'closed');
                    } catch (error) {}
                });
            }(detailsList[i]));
        }
    }

    function testConnection(instance) {
        var view = instance.view;
        var resultElement = view.querySelector('.connectionTestResult');
        var url = view.querySelector('.txtBaseUrl').value.replace(/\/+$/, '');
        var username = view.querySelector('.txtUsername').value;
        var password = view.querySelector('.txtPassword').value;

        resultElement.innerHTML = '<span style="opacity:0.5;">Testing connection...</span>';
        if (!url || !username || !password) {
            setPillResult(resultElement, false, 'Please enter server URL, username, and password.');
            return;
        }

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('M3uEditor/TestConnection'),
            contentType: 'application/json',
            data: JSON.stringify({
                Url: url,
                Username: username,
                Password: password,
                UserAgent: view.querySelector('.txtHttpUserAgent').value
            }),
            dataType: 'json'
        }).then(function (result) {
            setPillResult(resultElement, !!result.Success, result.Message || 'Connection test completed.');
            if (result.Success) saveConfig(instance);
        }).catch(function () {
            setPillResult(resultElement, false, 'Test request failed. Check server logs.');
        });
    }

    function checkProbeDataCoverage(view) {
        var resultElement = view.querySelector('.probeCoverageResult');
        resultElement.innerHTML = '<span style="opacity:0.5;">Checking probe data coverage&hellip;</span>';
        ApiClient.getJSON(ApiClient.getUrl('M3uEditor/ProbeDataCoverage')).then(function (result) {
            if (!result) {
                setPillResult(resultElement, false, 'Empty response from server.');
                return;
            }
            setPillResult(resultElement, result.Success !== false, result.Message || '');
        }).catch(function () {
            setPillResult(resultElement, false, 'Probe coverage request failed. Check server logs.');
        });
    }

    function loadCachedCategories(instance, config) {
        if (!config.CachedLiveCategories) return;
        try {
            var categories = JSON.parse(config.CachedLiveCategories);
            if (!categories || categories.length === 0) return;
            instance.loadedCategories = categories;
            renderCategoryList(instance.view, categories, instance.selectedCategoryIds);
            setCategoryButtonsDisabled(instance.view, false);
            updateCategoryCountBadge(instance.view);
        } catch (error) {}
    }

    function loadCategories(instance) {
        var view = instance.view;
        var list = view.querySelector('.categoriesList');
        var loadingElement = view.querySelector('.categoriesLoading');
        var statusElement = view.querySelector('.liveCategoriesStatus');

        loadingElement.style.display = 'block';
        list.innerHTML = '';
        statusElement.innerHTML = '<span style="opacity:0.6; font-size:0.9em;">Loading...</span>';

        ApiClient.getJSON(ApiClient.getUrl('M3uEditor/Categories/Live')).then(function (categories) {
            loadingElement.style.display = 'none';
            instance.loadedCategories = categories || [];
            if (!categories || categories.length === 0) {
                list.innerHTML = '<div style="opacity:0.5;">No categories found. Check your m3u-editor connection settings.</div>';
                setCategoryButtonsDisabled(view, true);
                setPillResult(statusElement, true, 'Refresh completed. No categories returned.');
                return;
            }
            renderCategoryList(view, categories, instance.selectedCategoryIds);
            setCategoryButtonsDisabled(view, false);
            updateCategoryCountBadge(view);
            setPillResult(statusElement, true, 'Loaded ' + categories.length + ' categories.');
        }).catch(function () {
            loadingElement.style.display = 'none';
            list.innerHTML = '<div style="color:#cc0000;">Failed to load categories. Save your connection settings first.</div>';
            setCategoryButtonsDisabled(view, true);
            setPillResult(statusElement, false, 'Failed to refresh categories. Save settings first, then retry.');
        });
    }

    function renderCategoryList(view, categories, selectedIds) {
        var html = '';
        for (var i = 0; i < categories.length; i++) {
            var category = categories[i];
            var checked = selectedIds.indexOf(category.CategoryId) >= 0 ? ' checked' : '';
            html += '<div class="checkboxContainer" style="margin:0.15em 0;">' +
                '<label style="display:flex; align-items:center; cursor:pointer;">' +
                '<input type="checkbox" class="categoryCheckbox" data-category-id="' + category.CategoryId + '"' + checked + ' style="margin-right:0.5em;" />' +
                '<span>' + escapeHtml(category.CategoryName) + '</span></label></div>';
        }
        view.querySelector('.categoriesList').innerHTML = html;
    }

    function setCategoryButtonsDisabled(view, disabled) {
        view.querySelector('.btnSelectAllCategories').disabled = disabled;
        view.querySelector('.btnDeselectAllCategories').disabled = disabled;
    }

    function toggleAllCategories(view, checked) {
        var checkboxes = view.querySelectorAll('.categoryCheckbox');
        for (var i = 0; i < checkboxes.length; i++) checkboxes[i].checked = checked;
        updateCategoryCountBadge(view);
    }

    function getSelectedCategoryIds(instance) {
        var checkboxes = instance.view.querySelectorAll('.categoryCheckbox');
        if (checkboxes.length === 0) return instance.selectedCategoryIds;

        var ids = [];
        for (var i = 0; i < checkboxes.length; i++) {
            if (checkboxes[i].checked) ids.push(parseInt(checkboxes[i].getAttribute('data-category-id'), 10));
        }
        return ids;
    }

    function setupCategorySearch(view) {
        var input = view.querySelector('.liveCategorySearch');
        input.addEventListener('input', function () {
            var filter = input.value.toLowerCase();
            var items = view.querySelectorAll('.categoriesList .checkboxContainer');
            for (var i = 0; i < items.length; i++) {
                items[i].style.display = items[i].textContent.toLowerCase().indexOf(filter) >= 0 ? '' : 'none';
            }
        });
    }

    function updateCategoryCountBadge(view) {
        var badge = view.querySelector('.liveCategoryCountBadge');
        var total = view.querySelectorAll('.categoryCheckbox').length;
        var selected = view.querySelectorAll('.categoryCheckbox:checked').length;
        if (total === 0) {
            badge.style.display = 'none';
            return;
        }
        badge.querySelector('.countSelected').textContent = selected;
        badge.querySelector('.countTotal').textContent = total;
        badge.style.display = '';
        badge.classList.toggle('zero-selected', selected === 0);
    }

    function refreshCache(view) {
        var resultElement = view.querySelector('.refreshCacheResult');
        resultElement.innerHTML = '<span style="opacity:0.5;">Refreshing cache...</span>';
        ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('M3uEditor/RefreshCache') }).then(function () {
            setPillResult(resultElement, true, 'Cache refreshed successfully!');
        }).catch(function () {
            setPillResult(resultElement, false, 'Failed to refresh cache.');
        });
    }

    function refreshChannelIcons(view) {
        var button = view.querySelector('.btnRefreshChannelIcons');
        var resultElement = view.querySelector('.refreshCacheResult');
        button.disabled = true;
        resultElement.innerHTML = '<span style="opacity:0.5;">Reloading channel icons...</span>';
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('M3uEditor/RefreshChannelIcons'),
            dataType: 'json'
        }).then(function (result) {
            button.disabled = false;
            var detail = result.Message || 'Channel icon reload completed.';
            if (result.Success) detail += ' Cleared: ' + result.ClearedChannels + ', rebuilt: ' + result.RebuiltChannels + '.';
            setPillResult(resultElement, result.Success, detail);
        }).catch(function () {
            button.disabled = false;
            setPillResult(resultElement, false, 'Channel icon reload failed. Check server logs.');
        });
    }

    function loadDashboard(view) {
        var url = ApiClient.getUrl('M3uEditor/Dashboard') +
            '?ManagedPage=' + encodeURIComponent(managedPage) + '&ManagedPageSize=10';
        ApiClient.getJSON(url).then(function (data) {
            loadDashboard._retries = 0;
            renderPluginVersion(view, data.PluginVersion);
            renderManagedPublishing(view, data.ManagedPublishing);
        }).catch(function (error) {
            console.warn('M3uEditor: dashboard load failed', error);
            var attempts = (loadDashboard._retries = (loadDashboard._retries || 0) + 1);
            if (attempts <= 5) {
                setTimeout(function () { loadDashboard(view); }, 4000);
                return;
            }
            var message = '<span class="status-badge failed">Unavailable</span>' +
                '<span style="margin-left:0.7em;">Managed publishing status unavailable.</span>';
            view.querySelector('.managedPublishingStatus').innerHTML = message;
            view.querySelector('.managedPublishingSummaryStatus').innerHTML = message;
            view.querySelector('.managedPublishingError').style.display = '';
            view.querySelector('.managedPublishingError').textContent = 'Reopen this page to retry.';
            view.querySelector('.btnManagedReconcile').disabled = true;
            view.querySelector('.btnManagedRollback').disabled = true;
        });
    }

    function renderPluginVersion(view, version) {
        if (!version) return;
        view.querySelector('.pluginVersion').textContent = 'v' + version;

        var previous = localStorage.getItem('m3u-editor-for-emby-plugin-version');
        if (previous && previous !== version && !sessionStorage.getItem('m3u-editor-for-emby-cache-bust')) {
            sessionStorage.setItem('m3u-editor-for-emby-cache-bust', '1');
            var appVersion = document.documentElement.getAttribute('data-appversion') || '';
            Promise.all([
                fetch('configurationpage?name=m3ueditorconfigr2&v=' + appVersion, { cache: 'reload' }),
                fetch('configurationpage?name=m3ueditorconfigjsr2&v=' + appVersion, { cache: 'reload' })
            ]).then(function () { location.reload(); });
            return;
        }
        localStorage.setItem('m3u-editor-for-emby-plugin-version', version);
        sessionStorage.removeItem('m3u-editor-for-emby-cache-bust');
    }

    function renderManagedPublishing(view, managed) {
        managed = managed || {};
        var summaryElement = view.querySelector('.managedPublishingSummaryStatus');
        var statusElement = view.querySelector('.managedPublishingStatus');
        var detailsElement = view.querySelector('.managedPublishingDetails');
        var mappingsElement = view.querySelector('.managedPublishingMappings');
        var warningElement = view.querySelector('.managedPublishingWarning');
        var errorElement = view.querySelector('.managedPublishingError');
        var selectElement = view.querySelector('.managedRollbackMapping');
        var rollbackButton = view.querySelector('.btnManagedRollback');
        var reconcileButton = view.querySelector('.btnManagedReconcile');
        var previousButton = view.querySelector('.btnManagedPreviousPage');
        var nextButton = view.querySelector('.btnManagedNextPage');
        var pageElement = view.querySelector('.managedPublishingPage');
        var resultElement = view.querySelector('.managedPublishingResult');
        var job = managed.Job || {};
        var jobRunning = job.State === 'running';
        var available = !!managed.SetupReady && !!managed.ConfigurationValid;

        if (!available) {
            summaryElement.innerHTML = '<span class="status-badge idle">Awaiting managed setup</span>' +
                '<span style="margin-left:0.7em;">Managed by m3u-editor.</span>';
            statusElement.innerHTML = '<span class="status-badge idle">Not connected</span>' +
                '<span style="margin-left:0.7em;">Complete setup from m3u-editor.</span>';
        } else if (!managed.Enabled) {
            summaryElement.innerHTML = '<span class="status-badge success">Managed setup ready</span>' +
                '<span style="margin-left:0.7em;">Managed by m3u-editor.</span>';
            statusElement.innerHTML = '<span class="status-badge idle">Connected</span>' +
                '<span style="margin-left:0.7em;">Waiting for a compatible publishing catalog.</span>';
        } else {
            summaryElement.innerHTML = '<span class="status-badge success">Managed mode active</span>' +
                '<span style="margin-left:0.7em;">' + escapeHtml(managed.TotalMappings || 0) + ' mapping(s), ' +
                escapeHtml(managed.TotalStrmFiles || 0) + ' managed stream files.</span>';
            statusElement.innerHTML = '<span class="status-badge success">Managed mode active</span>' +
                '<span style="margin-left:0.7em;">API v' + escapeHtml(managed.ApiVersion || '') +
                ': full snapshots, library mappings, variants, failover, and revision metadata</span>';
        }

        detailsElement.innerHTML =
            '<div><strong>Setup:</strong> ' + escapeHtml(managed.SetupResult || 'Not ready') + '</div>' +
            '<div><strong>Catalog:</strong> ' + escapeHtml(managed.CatalogRevision || 'Not fetched') + '</div>' +
            '<div><strong>Active generation:</strong> ' + escapeHtml(managed.ActiveGeneration || 'None') + '</div>' +
            '<div><strong>Previous generation:</strong> ' + escapeHtml(managed.PreviousGeneration || 'None') + '</div>' +
            '<div><strong>Owned files:</strong> ' + escapeHtml(managed.TotalFiles || 0) + '</div>' +
            '<div><strong>Dry run:</strong> ' + escapeHtml(managed.DryRunSummary || 'No plan available') + '</div>' +
            '<div><strong>Last success:</strong> ' + escapeHtml(managed.LastSuccess ? new Date(managed.LastSuccess).toLocaleString() : 'Never') + '</div>';

        var mappings = managed.Mappings || [];
        if (!managed.Mappings) {
            try {
                mappings = managed.MappingsJson ? JSON.parse(managed.MappingsJson) : [];
            } catch (error) {
                console.warn('M3uEditor: managed mapping state parse failed', error);
            }
        }
        if (mappings.length === 0) {
            mappingsElement.innerHTML = '<div style="opacity:0.55;">No managed library mappings advertised.</div>';
            selectElement.innerHTML = '<option value="">No mapping available</option>';
        } else {
            mappingsElement.innerHTML = mappings.map(function (mapping) {
                var state = mapping.Success ? 'success' : 'failed';
                var label = mapping.Success ? (mapping.Duplicate ? 'Already current' : 'Published') : 'Failed';
                return '<div style="padding:0.45em 0; border-top:1px solid rgba(128,128,128,0.12);">' +
                    '<span class="status-badge ' + state + '">' + label + '</span> ' +
                    '<strong>' + escapeHtml(mapping.LibraryName || mapping.MappingUuid) + '</strong> ' +
                    '<span style="opacity:0.6;">(' + escapeHtml(mapping.CollectionType || '') + ', revision ' +
                    escapeHtml(mapping.ActiveRevision || 'none') + ', ' + escapeHtml(mapping.FileCount || 0) + ' files)</span></div>';
            }).join('');
            selectElement.innerHTML = mappings.map(function (mapping) {
                return '<option value="' + escapeHtml(mapping.MappingUuid) + '">' +
                    escapeHtml(mapping.LibraryName || mapping.MappingUuid) + '</option>';
            }).join('');
        }

        warningElement.style.display = managed.OmittedVersions > 0 ? '' : 'none';
        warningElement.textContent = managed.OmittedVersions > 0
            ? managed.OmittedVersions + ' visible version(s) were omitted by the eight-version cap.' : '';
        errorElement.style.display = managed.LastError ? '' : 'none';
        errorElement.textContent = managed.LastError ? 'Last error: ' + managed.LastError : '';
        pageElement.textContent = managed.TotalMappings > 0
            ? 'Page ' + (managed.Page || 1) + ' of ' + Math.ceil(managed.TotalMappings / (managed.PageSize || 10)) : '';
        previousButton.disabled = !available || (managed.Page || 1) <= 1;
        nextButton.disabled = !available || !managed.HasMore;
        reconcileButton.disabled = jobRunning || !available;
        rollbackButton.disabled = jobRunning || !available || !managed.PreviousGeneration || mappings.length === 0;

        if (jobRunning) {
            setPillResult(resultElement, true, 'Managed ' + (job.Action || 'action') + ' is running.');
            if (!managedActionPollId) {
                managedActionPollId = setTimeout(function () {
                    managedActionPollId = null;
                    loadDashboard(view);
                }, 2000);
            }
        } else {
            if (managedActionPollId) {
                clearTimeout(managedActionPollId);
                managedActionPollId = null;
            }
            if ((job.State === 'succeeded' || job.State === 'failed') && job.Result) {
                setPillResult(resultElement, job.State === 'succeeded', job.Result.Message || 'Managed action finished.');
            }
        }
    }

    function reconcileManaged(view) {
        var resultElement = view.querySelector('.managedPublishingResult');
        var button = view.querySelector('.btnManagedReconcile');
        button.disabled = true;
        resultElement.textContent = 'Reconciling managed catalog...';
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('M3uEditor/Managed/Reconcile'),
            dataType: 'json'
        }).then(function (result) {
            setPillResult(resultElement, result.Success,
                result.Accepted ? 'Managed reconcile accepted.' : (result.Message || 'Managed reconcile is running.'));
            loadDashboard(view);
        }).catch(function (error) {
            console.error('M3uEditor: managed reconcile failed', error);
            button.disabled = false;
            setPillResult(resultElement, false, 'Managed reconcile request failed. Click Reconcile Now to retry.');
        });
    }

    function rollbackManaged(view) {
        var mappingUuid = view.querySelector('.managedRollbackMapping').value;
        if (!mappingUuid || !window.confirm('Restore the previous plugin-owned generation for this mapping?')) return;

        var resultElement = view.querySelector('.managedPublishingResult');
        var button = view.querySelector('.btnManagedRollback');
        button.disabled = true;
        resultElement.textContent = 'Restoring previous generation...';
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('M3uEditor/Managed/Rollback'),
            contentType: 'application/json',
            dataType: 'json',
            data: JSON.stringify({ MappingUuid: mappingUuid })
        }).then(function (result) {
            setPillResult(resultElement, result.Success,
                result.Accepted ? 'Managed rollback accepted.' : (result.Message || 'Managed action is running.'));
            loadDashboard(view);
        }).catch(function (error) {
            console.error('M3uEditor: managed rollback failed', error);
            button.disabled = false;
            setPillResult(resultElement, false, 'Managed rollback request failed. Select the mapping and retry.');
        });
    }

    function checkForUpdate(view) {
        ApiClient.getJSON(ApiClient.getUrl('M3uEditor/CheckUpdate')).then(function (data) {
            var banner = view.querySelector('.updateBanner');
            var versionElement = view.querySelector('.pluginVersion');
            if (data.CurrentVersion) {
                versionElement.textContent = 'v' + data.CurrentVersion;
            }

            if (data.UpdateInstalled) {
                banner.style.background = 'rgba(230,126,34,0.15)';
                banner.style.borderColor = 'rgba(230,126,34,0.4)';
                view.querySelector('.updateBannerTitle').textContent = 'Update Installed:';
                view.querySelector('.updateBannerText').textContent =
                    'v' + data.LatestVersion + ' has been installed. Restart Emby to apply.';
                view.querySelector('.btnInstallUpdate').style.display = 'none';
                view.querySelector('.btnRestartEmby').style.display = '';
                updateReleaseLink(view, data.ReleaseUrl);
                banner.style.display = 'block';
            } else if (data.UpdateAvailable) {
                view.querySelector('.updateBannerTitle').textContent = 'Update Available:';
                view.querySelector('.updateBannerText').textContent = 'v' + data.LatestVersion +
                    (data.IsPreRelease ? ' (beta)' : '') + ' is available (you have v' + data.CurrentVersion + ')';
                view.querySelector('.btnInstallUpdate').style.display = data.DownloadUrl ? '' : 'none';
                view.querySelector('.btnInstallUpdate').disabled = false;
                view.querySelector('.btnRestartEmby').style.display = 'none';
                updateReleaseLink(view, data.ReleaseUrl);
                banner.style.display = 'block';
            } else {
                banner.style.display = 'none';
            }
        }).catch(function (error) {
            console.error('M3uEditor: update check failed', error);
        });
    }

    function updateReleaseLink(view, url) {
        var link = view.querySelector('.updateBannerLink');
        link.style.display = url ? '' : 'none';
        if (url) link.href = url;
    }

    function installUpdate(view) {
        var button = view.querySelector('.btnInstallUpdate');
        var statusElement = view.querySelector('.updateStatus');
        button.disabled = true;
        button.textContent = 'Installing...';
        statusElement.style.display = 'block';
        statusElement.innerHTML = '<span style="opacity:0.5;">Downloading and installing update...</span>';
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('M3uEditor/InstallUpdate'),
            dataType: 'json'
        }).then(function (result) {
            if (result.Success) {
                statusElement.innerHTML = '<span style="color:#52B54B;">' + escapeHtml(result.Message) + '</span>';
                button.style.display = 'none';
                view.querySelector('.btnRestartEmby').style.display = '';
            } else {
                statusElement.innerHTML = '<span style="color:#cc0000;">' + escapeHtml(result.Message) + '</span>';
                button.disabled = false;
                button.textContent = 'Update Now';
            }
        }).catch(function () {
            statusElement.innerHTML = '<span style="color:#cc0000;">Install request failed. Check server logs.</span>';
            button.disabled = false;
            button.textContent = 'Update Now';
        });
    }

    function restartEmby(view) {
        if (!window.confirm('Are you sure you want to restart Emby? All active streams will be interrupted.')) return;

        var button = view.querySelector('.btnRestartEmby');
        var statusElement = view.querySelector('.updateStatus');
        button.disabled = true;
        button.textContent = 'Restarting...';
        statusElement.style.display = 'block';
        statusElement.innerHTML = '<span style="opacity:0.5;">Restarting Emby server...</span>';
        ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('M3uEditor/RestartEmby') }).then(function () {
            pollServerReady(view);
        }).catch(function () {
            pollServerReady(view);
        });
    }

    function pollServerReady(view) {
        var statusElement = view.querySelector('.updateStatus');
        var attempts = 0;
        statusElement.innerHTML = '<span style="opacity:0.5;">Waiting for server to come back...</span>';
        var pollId = setInterval(function () {
            attempts++;
            if (attempts > 60) {
                clearInterval(pollId);
                statusElement.innerHTML = '<span style="color:#cc0000;">Server did not come back within 2 minutes.</span>';
                return;
            }
            var request = new XMLHttpRequest();
            request.open('GET', ApiClient.getUrl('System/Info/Public'), true);
            request.timeout = 3000;
            request.onload = function () {
                if (request.status >= 200 && request.status < 300) {
                    clearInterval(pollId);
                    window.location.reload();
                }
            };
            request.send();
        }, 2000);
    }

    function renderHealthBar(view, config) {
        var item = view.querySelector('.healthItemXtream');
        var connected = !!(config.BaseUrl && config.Username);
        setHealthDot(item, connected ? 'ok' : 'grey');
        item.querySelector('.healthLabel').textContent = connected
            ? 'm3u-editor: Connected (' + config.Username + ')'
            : 'm3u-editor: Not configured';
    }

    function setHealthDot(item, status) {
        var colours = { ok: '#52B54B', grey: '#888' };
        item.querySelector('.healthDot').style.background = colours[status] || colours.grey;
    }

    function updateDashboardEmptyState(view, config) {
        view.querySelector('.dashboardEmptyStateUnconfigured').style.display =
            config.BaseUrl && config.Username ? 'none' : '';
    }

    function setPillResult(element, success, message) {
        var cssClass = success ? 'success' : 'error';
        var icon = success ? '\u2713' : '\u2717';
        element.innerHTML = '<span class="result-pill ' + cssClass + '">' + icon + '  ' + escapeHtml(message || '') + '</span>';
    }

    function escapeHtml(value) {
        var div = document.createElement('div');
        div.appendChild(document.createTextNode(value == null ? '' : String(value)));
        return div.innerHTML;
    }

    return View;
});
