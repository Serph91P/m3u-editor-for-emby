'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const configScriptPath = process.argv[2];
const configHtmlPath = process.argv[3];
if (!configScriptPath || !configHtmlPath) throw new Error('Expected config.js and config.html paths.');
const configScript = fs.readFileSync(configScriptPath, 'utf8');
const configHtml = fs.readFileSync(configHtmlPath, 'utf8');

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
        this.classList = { contains: () => false, toggle: () => {} };
    }

    addEventListener(name, handler) {
        (this.events[name] || (this.events[name] = [])).push(handler);
    }

    emit(name) {
        const event = { target: this, preventDefault: () => {} };
        for (const handler of this.events[name] || []) handler.call(this, event);
    }

    querySelector(selector) {
        return this.root.lookup(selector);
    }

    querySelectorAll() {
        return [];
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
    }

    lookup(selector) {
        if (!this.elements.has(selector)) this.elements.set(selector, new FakeElement(this));
        return this.elements.get(selector);
    }
}

function storage() {
    return { getItem: () => null, setItem: () => {}, removeItem: () => {} };
}

async function flushPromises() {
    await new Promise(resolve => setImmediate(resolve));
    await new Promise(resolve => setImmediate(resolve));
}

async function mount(options) {
    const view = new FakeView();
    const state = {
        config: Object.assign({
            BaseUrl: '', Username: '', Password: '', HttpUserAgent: '',
            EnableLiveTv: true, LiveTvOutputFormat: 'ts', SelectedLiveCategoryIds: [],
            EpgSource: 0, EpgCacheMinutes: 30, EpgDaysToFetch: 2, M3UCacheMinutes: 15
        }, options.config || {}),
        dashboard: options.dashboard || {},
        updates: [],
        alerts: [],
        configReads: 0
    };

    global.window = global;
    global.localStorage = storage();
    global.sessionStorage = storage();
    global.location = { reload: () => {} };
    global.document = {
        documentElement: { getAttribute: () => '' },
        createTextNode: value => ({ value: String(value) }),
        createElement: () => ({
            innerHTML: '',
            appendChild(node) { this.innerHTML = node.value; }
        })
    };
    global.Dashboard = {
        alert: message => state.alerts.push(message),
        processPluginConfigurationUpdateResult: () => state.saved = true
    };
    global.loading = { show: () => {}, hide: () => {} };
    global.ApiClient = {
        getPluginConfiguration: () => {
            state.configReads++;
            return state.configReads === options.rejectConfigReadAt
                ? Promise.reject(new Error('configuration unavailable'))
                : Promise.resolve(JSON.parse(JSON.stringify(state.config)));
        },
        updatePluginConfiguration: (_id, config) => {
            state.updates.push(JSON.parse(JSON.stringify(config)));
            if (options.rejectSave) return Promise.reject(new Error('output directory is in use'));
            state.config = JSON.parse(JSON.stringify(config));
            if (options.normalizedRoot) state.config.ManagedPublishingOutputRoot = options.normalizedRoot;
            return Promise.resolve({});
        },
        getJSON: () => Promise.resolve({ ManagedPublishing: state.dashboard }),
        getUrl: path => '/admin/' + path,
        ajax: () => Promise.resolve({}),
        accessToken: () => 'test-token'
    };

    function BaseView(element) { this.view = element; }
    BaseView.prototype.onResume = function () {};
    let View;
    global.define = (_dependencies, factory) => { View = factory(BaseView, global.loading); };
    vm.runInThisContext(configScript, { filename: configScriptPath });
    const instance = new View(view);
    instance.onResume();
    await flushPromises();
    return { view, state };
}

function managedControls() {
    const panel = configHtml.match(/<div class="tabPanel tabManagedPublishing"[\s\S]*?<\/div><!-- end tabManagedPublishing -->/);
    assert.ok(panel, 'managed publishing panel must exist');
    return Array.from(panel[0].matchAll(/<(input|select|button)\b([^>]*)>/g))
        .map(match => ({ tag: match[1], attributes: match[2] }))
        .filter(control => /\bclass="[^"]*(?:txtManagedPublishingOutputRoot|btnManaged|managedRollbackMapping|managedViewBtn)[^"]*"/.test(control.attributes));
}

async function run() {
    const controls = managedControls();
    const controlIdentities = controls.map(control => {
        var managedView = control.attributes.match(/data-managed-view="([^"]+)"/);
        var className = control.attributes.match(/class="([^"]+)"/);
        return control.tag + ':' + (managedView ? managedView[1] : className[1]);
    });
    const uniqueControls = new Set(controlIdentities);
    assert.equal(controls.length, uniqueControls.size, 'managed controls must have unique identities');
    assert.ok(controlIdentities.some(identity => identity.indexOf('txtManagedPublishingOutputRoot') >= 0), 'output-root input must be present');
    assert.ok(uniqueControls.size >= 9, 'managed publishing controls must be present');

    for (const control of controls) {
        assert.match(control.attributes, /\btitle="[^"]+"/, 'control needs a tooltip: ' + control.attributes);
    }

    let mounted = await mount({
        config: { ManagedPublishingOutputRoot: '/config/managed' },
        dashboard: { OutputRoot: '/effective/managed' }
    });
    assert.equal(mounted.view.lookup('.txtManagedPublishingOutputRoot').value, '/config/managed');
    assert.equal(mounted.view.lookup('.managedPublishingEffectiveOutputRoot').textContent, '/effective/managed');

    mounted.view.lookup('.txtManagedPublishingOutputRoot').value = '/changed/managed';
    mounted.view.lookup('.m3uEditorConfigForm').emit('submit');
    await flushPromises();
    assert.equal(mounted.state.updates.length, 1);
    assert.equal(mounted.state.updates[0].ManagedPublishingOutputRoot, '/changed/managed');
    assert.equal(mounted.state.saved, true);

    mounted = await mount({ config: { ManagedPublishingOutputRoot: '/config/managed' }, rejectSave: true });
    mounted.view.lookup('.txtManagedPublishingOutputRoot').value = '/blocked/managed';
    mounted.view.lookup('.m3uEditorConfigForm').emit('submit');
    await flushPromises();
    assert.equal(mounted.view.lookup('.txtManagedPublishingOutputRoot').value, '/blocked/managed');
    assert.match(mounted.view.lookup('.managedPublishingOutputRootResult').innerHTML, /Failed to save managed publishing output directory/);
    assert.equal(mounted.state.saved, undefined);

    mounted = await mount({ dashboard: { OutputRoot: '<unsafe-root>' } });
    assert.equal(mounted.view.lookup('.managedPublishingEffectiveOutputRoot').textContent, '<unsafe-root>');
    assert.equal(mounted.view.lookup('.managedPublishingEffectiveOutputRoot').innerHTML, '');

    mounted = await mount({ config: { ManagedPublishingOutputRoot: '/config/managed', LiveTvTunerCount: 4 } });
    mounted.state.config.ManagedAllowedMediaRoots = '/fresh/server/binding';
    const beforeRootSave = JSON.parse(JSON.stringify(mounted.state.config));
    mounted.view.lookup('.txtManagedPublishingOutputRoot').value = '/root-only/managed';
    mounted.view.lookup('.txtLiveTvTunerCount').value = 'invalid-unsaved-value';
    mounted.view.lookup('.txtBaseUrl').value = 'unsaved-connection-edit';
    mounted.view.lookup('.btnManagedSaveOutputRoot').emit('click');
    await flushPromises();
    assert.equal(mounted.state.updates.length, 1, 'Managed tab must save its root without submitting unrelated Settings');
    assert.deepEqual(mounted.state.updates[0], Object.assign({}, beforeRootSave, { ManagedPublishingOutputRoot: '/root-only/managed' }));
    assert.equal(mounted.view.lookup('.txtBaseUrl').value, 'unsaved-connection-edit');
    assert.ok(controlIdentities.some(identity => identity.includes('btnManagedSaveOutputRoot')), 'save action must actually be rendered inside the Managed tab');

    mounted = await mount({ config: { ManagedPublishingOutputRoot: '/unconfirmed/managed' } });
    assert.equal(mounted.view.lookup('.managedPublishingEffectiveOutputRoot').textContent,
        'Not confirmed yet. Complete managed setup from m3u-editor.');

    mounted = await mount({ config: { ManagedPublishingOutputRoot: '/config/managed' }, rejectSave: true });
    mounted.view.lookup('.txtManagedPublishingOutputRoot').value = '/blocked/managed';
    mounted.view.lookup('.btnManagedSaveOutputRoot').emit('click');
    mounted.view.lookup('.btnManagedSaveOutputRoot').emit('click');
    await flushPromises();
    assert.equal(mounted.state.updates.length, 1, 'in-flight root save must not run twice');
    assert.equal(mounted.view.lookup('.txtManagedPublishingOutputRoot').value, '/blocked/managed');
    assert.match(mounted.view.lookup('.managedPublishingOutputRootResult').innerHTML, /Failed to save managed publishing output directory/);
    assert.equal(mounted.view.lookup('.btnManagedSaveOutputRoot').disabled, false);
    assert.equal(mounted.view.lookup('.txtManagedPublishingOutputRoot').disabled, false);

    mounted = await mount({ normalizedRoot: '/canonical/managed' });
    mounted.view.lookup('.txtManagedPublishingOutputRoot').value = '/canonical/managed/.';
    mounted.view.lookup('.btnManagedSaveOutputRoot').emit('click');
    await flushPromises();
    assert.equal(mounted.state.configReads, 3, 'save must fetch fresh config and read back the stored result');
    assert.equal(mounted.view.lookup('.txtManagedPublishingOutputRoot').value, '/canonical/managed');

    mounted = await mount({ rejectConfigReadAt: 2 });
    mounted.view.lookup('.txtManagedPublishingOutputRoot').value = '/retained/managed';
    mounted.view.lookup('.btnManagedSaveOutputRoot').emit('click');
    await flushPromises();
    assert.equal(mounted.state.updates.length, 0);
    assert.equal(mounted.view.lookup('.txtManagedPublishingOutputRoot').value, '/retained/managed');
    assert.match(mounted.view.lookup('.managedPublishingOutputRootResult').innerHTML, /Failed to save/);
    assert.equal(mounted.view.lookup('.btnManagedSaveOutputRoot').disabled, false);

    mounted = await mount({ rejectConfigReadAt: 3 });
    mounted.view.lookup('.txtManagedPublishingOutputRoot').value = '/persisted/managed';
    mounted.view.lookup('.btnManagedSaveOutputRoot').emit('click');
    await flushPromises();
    assert.equal(mounted.state.updates.length, 1);
    assert.equal(mounted.state.config.ManagedPublishingOutputRoot, '/persisted/managed');
    assert.match(mounted.view.lookup('.managedPublishingOutputRootResult').innerHTML, /was saved, but could not be reloaded/);
    assert.equal(mounted.state.saved, undefined, 'unverified write must not show the success toast');

    process.stdout.write('Issue105 managed-output UI harness: 14 scenarios passed; ' + uniqueControls.size + ' unique managed controls checked\n');
}

run().catch(error => {
    console.error(error && error.stack ? error.stack : error);
    process.exitCode = 1;
});
