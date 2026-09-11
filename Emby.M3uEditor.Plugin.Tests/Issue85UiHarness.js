'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const configScriptPath = process.argv[2];
if (!configScriptPath) throw new Error('Expected the config.js path.');
const configScript = fs.readFileSync(configScriptPath, 'utf8');

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
        const event = {
            target: this,
            preventDefault: () => {},
        };
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
        updates: [],
        ajaxCalls: [],
        ajaxResult: options.ajaxResult || { Success: false },
        rejectAjax: !!options.rejectAjax,
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
        alert: () => {},
        processPluginConfigurationUpdateResult: () => {},
    };
    global.loading = { show: () => {}, hide: () => {} };
    global.ApiClient = {
        getPluginConfiguration: () => Promise.resolve(JSON.parse(JSON.stringify(state.config))),
        updatePluginConfiguration: (_id, config) => {
            state.updates.push(JSON.parse(JSON.stringify(config)));
            return Promise.resolve({});
        },
        ajax: request => {
            state.ajaxCalls.push(request);
            return state.rejectAjax ? Promise.reject(new Error('network')) : Promise.resolve(state.ajaxResult);
        },
        getJSON: () => Promise.resolve({}),
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

    process.stdout.write('Issue85 UI harness: 20 scenarios passed\n');
}

run().catch(error => {
    console.error(error && error.stack ? error.stack : error);
    process.exitCode = 1;
});
