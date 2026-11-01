'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const configScriptPath = process.argv[2];
if (!configScriptPath) throw new Error('Expected the config.js path.');
const configScript = fs.readFileSync(configScriptPath, 'utf8');
const configHtmlPath = process.argv[3];

function escaped(value) {
    return String(value)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

class FakeElement {
    constructor(root) {
        this.root = root || this;
        this.events = Object.create(null);
        this.attributes = Object.create(null);
        this.style = Object.create(null);
        this.value = '';
        this.checked = false;
        this.disabled = false;
        this.innerHTML = '';
        this.textContent = '';
        this.classList = {
            contains: () => false,
            toggle: () => {},
        };
    }

    addEventListener(name, handler) {
        (this.events[name] || (this.events[name] = [])).push(handler);
    }

    emit(name) {
        let prevented = false;
        const event = {
            target: this,
            preventDefault: () => { prevented = true; },
        };
        for (const handler of this.events[name] || []) handler.call(this, event);
        return prevented;
    }

    click() {
        assert.equal(this.tagName, 'button', 'clicked control must exist in the actual HTML');
        assert.equal(this.disabled, false, 'clicked button must be enabled');
        if (this.inFooter) assert.notEqual(this.root.lookup('.stickyFooter').style.display, 'none', 'Save must be visible on the managed tab');
        if (!this.emit('click') && this.getAttribute('type') === 'submit') this.root.lookup('.m3uEditorConfigForm').emit('submit');
    }

    querySelector(selector) {
        return this.root.lookup(selector);
    }

    querySelectorAll(selector) {
        return this.root.buttons ? this.root.buttons.filter(button => button.classes.includes(selector.slice(1))) : [];
    }

    getAttribute(name) {
        return this.attributes[name] || null;
    }

    setAttribute(name, value) {
        this.attributes[name] = String(value);
    }
}

class FakeView extends FakeElement {
    constructor() {
        super();
        this.root = this;
        this.elements = new Map();
        this.buttons = [];
        if (configHtmlPath) {
            const html = fs.readFileSync(configHtmlPath, 'utf8');
            for (const match of html.matchAll(/<button\b([^>]*)>/g)) {
                const button = new FakeElement(this);
                button.tagName = 'button';
                for (const attr of match[1].matchAll(/([\w-]+)="([^"]*)"/g)) button.setAttribute(attr[1], attr[2]);
                button.classes = (button.getAttribute('class') || '').split(/\s+/);
                button.inFooter = match.index > html.indexOf('<div class="stickyFooter">');
                for (const className of button.classes) this.elements.set('.' + className, button);
                this.buttons.push(button);
            }
        }
    }

    lookup(selector) {
        if (!this.elements.has(selector)) this.elements.set(selector, new FakeElement(this));
        return this.elements.get(selector);
    }
}

function makeStorage() {
    const values = new Map();
    return {
        getItem: key => values.has(key) ? values.get(key) : null,
        setItem: (key, value) => values.set(key, String(value)),
        removeItem: key => values.delete(key),
    };
}

function defaultConfig() {
    return {
        BaseUrl: 'https://configured.example',
        Username: 'configured-user',
        Password: 'configured-password',
        HttpUserAgent: 'configured-agent',
        EnableLiveTv: true,
        LiveTvOutputFormat: 'ts',
        SelectedLiveCategoryIds: [],
        EpgSource: 0,
        EpgCacheMinutes: 30,
        EpgDaysToFetch: 2,
        M3UCacheMinutes: 15,
    };
}

async function flushPromises() {
    await new Promise(resolve => setImmediate(resolve));
    await new Promise(resolve => setImmediate(resolve));
}

async function mount(options = {}) {
    const view = new FakeView();
    const state = {
        config: Object.assign(defaultConfig(), options.config || {}),
        dashboard: options.dashboard || {},
        updates: [],
        ajaxCalls: [],
        ajaxResult: options.ajaxResult || { Success: false },
        rejectAjax: !!options.rejectAjax,
        alerts: [],
    };

    global.window = global;
    global.localStorage = makeStorage();
    global.sessionStorage = makeStorage();
    global.location = { reload: () => {} };
    global.document = {
        documentElement: { getAttribute: () => '' },
        createTextNode: value => ({ value: escaped(value) }),
        createElement: () => ({
            innerHTML: '',
            appendChild(node) { this.innerHTML = node.value; },
        }),
    };
    global.Dashboard = {
        alert: message => state.alerts.push(message),
        processPluginConfigurationUpdateResult: () => {},
    };
    global.loading = { show: () => {}, hide: () => {} };
    global.ApiClient = {
        getPluginConfiguration: () => Promise.resolve(JSON.parse(JSON.stringify(state.config))),
        updatePluginConfiguration: (_id, config) => {
            state.updates.push(JSON.parse(JSON.stringify(config)));
            if (options.rejectSave) return Promise.reject(new Error('persistence unavailable'));
            state.config = JSON.parse(JSON.stringify(config));
            return Promise.resolve({});
        },
        ajax: request => {
            state.ajaxCalls.push(request);
            return state.rejectAjax ? Promise.reject(new Error('network')) : Promise.resolve(state.ajaxResult);
        },
        getJSON: () => Promise.resolve(state.dashboard),
        getUrl: path => '/admin/' + path,
        accessToken: () => 'test-token',
    };

    function BaseView(element) {
        this.view = element;
    }
    BaseView.prototype.onResume = function () {};

    let View;
    global.define = (_dependencies, factory) => {
        View = factory(BaseView, global.loading);
    };
    vm.runInThisContext(configScript, { filename: configScriptPath });
    assert.equal(typeof View, 'function', 'config.js must register its AMD view');

    const instance = new View(view);
    instance.onResume();
    await flushPromises();
    return { view, state };
}

async function saveWithValue(value) {
    const mounted = await mount();
    mounted.view.lookup('.txtLiveTvTunerCount').value = value;
    mounted.view.lookup('.m3uEditorConfigForm').emit('submit');
    await flushPromises();
    return mounted;
}

async function importWith(options) {
    const mounted = await mount(options);
    mounted.view.lookup('.txtBaseUrl').value = 'https://unsaved.example/';
    mounted.view.lookup('.txtUsername').value = 'unsaved-user';
    mounted.view.lookup('.txtPassword').value = 'unsaved-password';
    mounted.view.lookup('.txtHttpUserAgent').value = 'Unsaved-Agent/85';
    mounted.view.lookup('.txtLiveTvTunerCount').value = options.currentValue || '8';
    mounted.view.lookup('.btnImportXtreamLimit').emit('click');
    await flushPromises();
    return mounted;
}

async function runManagedPlaybackCacheLoadSaveScenario() {
    if (!configHtmlPath) return;

    const configHtml = fs.readFileSync(configHtmlPath, 'utf8');
    assert.match(configHtml, /class="chkManagedPlaybackCacheEnabled"/);
    assert.match(configHtml, /class="selManagedPlaybackCacheMode"/);
    assert.match(configHtml, /class="txtManagedPlaybackCacheUserIds"/);

    const mounted = await mount({
        config: {
            ManagedPlaybackCacheEnabled: true,
            ManagedPlaybackCacheMode: 'next_n',
            ManagedPlaybackCacheNextCount: 3,
            ManagedPlaybackCacheDeleteAfterWatched: true,
            ManagedPlaybackCacheDeleteGraceHours: 24,
            ManagedPlaybackCacheUserIds: ['profile-a', 'profile-b'],
        },
    });
    assert.equal(mounted.view.lookup('.chkManagedPlaybackCacheEnabled').checked, true);
    assert.equal(mounted.view.lookup('.selManagedPlaybackCacheMode').value, 'next_n');
    assert.equal(mounted.view.lookup('.txtManagedPlaybackCacheNextCount').value, 3);
    assert.equal(mounted.view.lookup('.chkManagedPlaybackCacheDeleteAfterWatched').checked, true);
    assert.equal(mounted.view.lookup('.txtManagedPlaybackCacheDeleteGraceHours').value, 24);
    assert.equal(mounted.view.lookup('.txtManagedPlaybackCacheUserIds').value, 'profile-a\nprofile-b');
    assert.equal(mounted.view.lookup('.managedPlaybackCacheNextCount').style.display, '');
    assert.equal(mounted.view.lookup('.managedPlaybackCacheRetention').style.display, '');

    mounted.view.lookup('.selManagedPlaybackCacheMode').value = 'whole_season';
    mounted.view.lookup('.selManagedPlaybackCacheMode').emit('change');
    mounted.view.lookup('.chkManagedPlaybackCacheDeleteAfterWatched').checked = false;
    mounted.view.lookup('.chkManagedPlaybackCacheDeleteAfterWatched').emit('change');
    assert.equal(mounted.view.lookup('.managedPlaybackCacheNextCount').style.display, 'none');
    assert.equal(mounted.view.lookup('.managedPlaybackCacheRetention').style.display, 'none');

    mounted.view.lookup('.txtManagedPlaybackCacheUserIds').value = ' profile-c \n\nprofile-d ';
    mounted.view.lookup('.m3uEditorConfigForm').emit('submit');
    await flushPromises();
    assert.equal(mounted.state.updates.length, 1);
    assert.deepEqual(mounted.state.updates[0].ManagedPlaybackCacheUserIds, ['profile-c', 'profile-d']);
    assert.equal(mounted.state.updates[0].ManagedPlaybackCacheMode, 'whole_season');
    assert.equal(mounted.state.updates[0].ManagedPlaybackCacheEnabled, true);
    assert.equal(mounted.state.updates[0].ManagedPlaybackCacheDeleteAfterWatched, false);
    process.stdout.write('Managed playback cache load/save scenario passed\n');
}

async function runManagedPlaybackCacheVisibleSaveScenario() {
    if (!configHtmlPath) return;
    const ready = { ManagedPublishing: { SetupReady: true, ConfigurationValid: true, CacheCapabilityReady: true, ActiveManifestIdentityReady: true } };
    const mounted = await mount({ dashboard: ready });
    mounted.view.lookup('.tabBtnManagedPublishing').click();
    assert.equal(mounted.view.lookup('.tabManagedPublishing').style.display, 'block');
    mounted.view.lookup('.chkManagedPlaybackCacheEnabled').checked = true;
    mounted.view.lookup('.selManagedPlaybackCacheMode').value = 'next_n';
    mounted.view.lookup('.txtManagedPlaybackCacheNextCount').value = '5';
    mounted.view.lookup('.chkManagedPlaybackCacheDeleteAfterWatched').checked = true;
    mounted.view.lookup('.txtManagedPlaybackCacheDeleteGraceHours').value = '48';
    mounted.view.lookup('.txtManagedPlaybackCacheUserIds').value = ' profile-c\nprofile-d ';
    mounted.view.lookup('.button-submit').click();
    await flushPromises();
    assert.equal(mounted.state.updates.length, 1);
    const reloaded = await mount({ config: mounted.state.config, dashboard: ready });
    assert.equal(reloaded.view.lookup('.chkManagedPlaybackCacheEnabled').checked, true);
    assert.equal(reloaded.view.lookup('.selManagedPlaybackCacheMode').value, 'next_n');
    assert.equal(reloaded.view.lookup('.txtManagedPlaybackCacheNextCount').value, 5);
    assert.equal(reloaded.view.lookup('.chkManagedPlaybackCacheDeleteAfterWatched').checked, true);
    assert.equal(reloaded.view.lookup('.txtManagedPlaybackCacheDeleteGraceHours').value, 48);
    assert.equal(reloaded.view.lookup('.txtManagedPlaybackCacheUserIds').value, 'profile-c\nprofile-d');
    const unavailable = await mount({ dashboard: { ManagedPublishing: { SetupReady: true } } });
    unavailable.view.lookup('.tabBtnManagedPublishing').click();
    assert.equal(unavailable.view.lookup('.chkManagedPlaybackCacheEnabled').disabled, true);
    unavailable.view.lookup('.chkManagedPlaybackCacheEnabled').checked = true;
    unavailable.view.lookup('.button-submit').click();
    await flushPromises();
    assert.equal(unavailable.state.config.ManagedPlaybackCacheEnabled, false);
    const failure = await mount({ dashboard: ready, rejectSave: true });
    failure.view.lookup('.tabBtnManagedPublishing').click();
    failure.view.lookup('.chkManagedPlaybackCacheEnabled').checked = true;
    failure.view.lookup('.button-submit').click();
    await flushPromises();
    assert.equal(failure.state.config.ManagedPlaybackCacheEnabled, undefined);
    assert.ok(failure.state.alerts.some(message => message.includes('Failed to save configuration')));
    process.stdout.write('Managed playback cache visible navigation/click/reload and failure scenarios passed\n');
}

async function runManagedPlaybackCacheReadinessScenario() {
    const baseManaged = {
        SetupReady: true,
        ConfigurationValid: true,
        Enabled: true,
        ActiveGeneration: 'a'.repeat(64),
        Mappings: [],
    };
    const unavailable = await mount({ dashboard: { ManagedPublishing: baseManaged } });
    const unavailableToggle = unavailable.view.lookup('.chkManagedPlaybackCacheEnabled');
    assert.equal(unavailableToggle.disabled, true, 'unknown cache capability must disable activation');
    unavailableToggle.checked = true;
    unavailable.view.lookup('.m3uEditorConfigForm').emit('submit');
    await flushPromises();
    assert.equal(unavailable.state.updates[0].ManagedPlaybackCacheEnabled, false,
        'unavailable readiness must not be saved as a new activation');

    const missingIdentity = await mount({ dashboard: { ManagedPublishing: Object.assign({}, baseManaged, {
        CacheCapabilityReady: true,
        ActiveManifestIdentityReady: false,
    }) } });
    assert.equal(missingIdentity.view.lookup('.chkManagedPlaybackCacheEnabled').disabled, true,
        'missing active identity must disable activation');

    const ready = await mount({ dashboard: { ManagedPublishing: Object.assign({}, baseManaged, {
        CacheCapabilityReady: true,
        ActiveManifestIdentityReady: true,
    }) } });
    const readyToggle = ready.view.lookup('.chkManagedPlaybackCacheEnabled');
    assert.equal(readyToggle.disabled, false, 'supported capability and active identity must enable activation');
    readyToggle.checked = true;
    ready.view.lookup('.m3uEditorConfigForm').emit('submit');
    await flushPromises();
    assert.equal(ready.state.updates[0].ManagedPlaybackCacheEnabled, true);

    const enabledButUnknown = await mount({
        config: { ManagedPlaybackCacheEnabled: true },
        dashboard: { ManagedPublishing: baseManaged },
    });
    const enabledToggle = enabledButUnknown.view.lookup('.chkManagedPlaybackCacheEnabled');
    assert.equal(enabledToggle.disabled, false, 'an existing activation must remain switch-off capable');
    enabledToggle.checked = false;
    enabledButUnknown.view.lookup('.m3uEditorConfigForm').emit('submit');
    await flushPromises();
    assert.equal(enabledButUnknown.state.updates[0].ManagedPlaybackCacheEnabled, false);
    process.stdout.write('Managed playback cache readiness scenario passed\n');
}

async function run() {
    let mounted = await mount({ config: { LiveTvTunerCount: 0 } });
    assert.equal(mounted.view.lookup('.txtLiveTvTunerCount').value, '');

    mounted = await mount({ config: { LiveTvTunerCount: 77 } });
    assert.equal(mounted.view.lookup('.txtLiveTvTunerCount').value, '77');

    mounted = await saveWithValue('   ');
    assert.equal(mounted.state.updates.length, 1);
    assert.equal(mounted.state.updates[0].LiveTvTunerCount, 0);

    mounted = await saveWithValue(' 42 ');
    assert.equal(mounted.state.updates.length, 1);
    assert.equal(mounted.state.updates[0].LiveTvTunerCount, 42);

    for (const invalid of ['0', '-1', '1.5', '1e3', 'junk', '2147483648']) {
        mounted = await saveWithValue(invalid);
        assert.equal(mounted.state.updates.length, 0, 'invalid value was saved: ' + invalid);
        assert.match(mounted.view.lookup('.livetvTunerCountValidation').innerHTML, /positive whole number/);
    }

    mounted = await importWith({
        config: { LiveTvTunerCount: 8 },
        ajaxResult: { Success: true, MaxConnections: 99 },
    });
    assert.equal(mounted.view.lookup('.txtLiveTvTunerCount').value, '99');
    assert.equal(mounted.state.ajaxCalls.length, 1);
    assert.equal(mounted.state.updates.length, 0);
    assert.equal(mounted.state.ajaxCalls[0].type, 'POST');
    assert.equal(mounted.state.ajaxCalls[0].url, '/admin/M3uEditor/TestConnection');
    assert.deepEqual(JSON.parse(mounted.state.ajaxCalls[0].data), {
        Url: 'https://unsaved.example',
        Username: 'unsaved-user',
        Password: 'unsaved-password',
        UserAgent: 'Unsaved-Agent/85',
    });

    const retainedResponses = [
        { ajaxResult: { Success: true } },
        { ajaxResult: { Success: false, MaxConnections: 9 } },
        { ajaxResult: { Success: true, MaxConnections: 0 } },
        { ajaxResult: { Success: true, MaxConnections: -1 } },
        { ajaxResult: { Success: true, MaxConnections: 1.5 } },
        { ajaxResult: { Success: true, MaxConnections: 2147483648 } },
        { ajaxResult: { Success: true, MaxConnections: '9' } },
        { rejectAjax: true },
    ];
    for (const response of retainedResponses) {
        mounted = await importWith(Object.assign({
            config: { LiveTvTunerCount: 8 },
            currentValue: ' 008 ',
        }, response));
        assert.equal(mounted.view.lookup('.txtLiveTvTunerCount').value, ' 008 ');
        assert.equal(mounted.state.ajaxCalls.length, 1);
        assert.equal(mounted.state.updates.length, 0);
        assert.match(mounted.view.lookup('.livetvLimitImportResult').innerHTML, /Could not import tuner limit/);
        assert.doesNotMatch(mounted.view.lookup('.livetvLimitImportResult').innerHTML, /unsaved-password/);
    }

    mounted = await mount({ config: { LiveTvTunerCount: 8 } });
    mounted.view.lookup('.txtBaseUrl').value = '';
    mounted.view.lookup('.txtLiveTvTunerCount').value = 'custom-current-value';
    mounted.view.lookup('.btnImportXtreamLimit').emit('click');
    await flushPromises();
    assert.equal(mounted.state.ajaxCalls.length, 0);
    assert.equal(mounted.view.lookup('.txtLiveTvTunerCount').value, 'custom-current-value');
    assert.match(mounted.view.lookup('.livetvLimitImportResult').innerHTML, /enter server URL/);

    await runManagedPlaybackCacheLoadSaveScenario();
    await runManagedPlaybackCacheVisibleSaveScenario();
    await runManagedPlaybackCacheReadinessScenario();

    process.stdout.write('Issue85 UI harness: 21 scenarios passed\n');
}

run().catch(error => {
    console.error(error && error.stack ? error.stack : error);
    process.exitCode = 1;
});
