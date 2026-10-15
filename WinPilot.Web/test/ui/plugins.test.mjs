import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';
import * as apiErrors from '../support/apiErrors.mjs';
import { combinePageSignal } from '../../src/features/plugins/page/PluginPageLifetime.ts';

function evaluate(path, require, members = '', globals = {}) {
  const code = loadScriptModule(new URL(path, import.meta.url), members);
  const context = { exports: {}, require, AbortController, DOMException, console, sessionStorage: { getItem: () => null, removeItem() {} }, EventSource: class { close() {} }, ...globals };
  runInNewContext(code, context);
  return context.exports;
}

function fixture(page = 'PluginInfoView.vue', requestGlobals = {}) {
  const calls = [], toasts = [], renders = [];
  const safeModeEnabled = vue.ref(false);
  const route = vue.reactive({ params: { pluginId: 'first' }, fullPath: '/plugins/first' });
  let unmount;
  let disposed = false;
  const client = {
    ...apiErrors,
    WinPilotConnectionError: class extends Error {},
    apiFetch: (url, init) => new Promise((resolve, reject) => calls.push({ url, init, resolve, reject })),
    isAbortError: error => error?.name === 'AbortError',
    userErrorMessage: (error, fallback) => error.message || fallback,
  };
  const executor = evaluate('../../src/api/requestExecutor.ts', name => name.endsWith('/client') ? client : { combineRequestSignal: combinePageSignal });
  const requests = evaluate('../../src/features/plugins/PluginRequests.ts', () => executor, '', requestGlobals);
  const models = evaluate('../../src/features/plugins/PluginModels.ts', () => ({}));
  const imports = name => {
    if (name === 'vue') return { ...vue, onMounted() {}, onBeforeUnmount: callback => { unmount = callback; } };
    if (name === 'vue-router') return { useRoute: () => route, useRouter: () => ({ push: async () => {} }) };
    if (name.endsWith('/PluginRequests')) return requests;
    if (name.endsWith('/PluginModels')) return models;
    if (name.endsWith('/client')) return client;
    if (name.endsWith('/toast')) return { showToast: (text, type) => toasts.push({ text, type }) };
    if (name.endsWith('/markdown')) return { renderMarkdown: source => new Promise((resolve, reject) => renders.push({ source, resolve, reject })) };
    if (name.endsWith('/safeMode')) return { safeModeEnabled };
    return {};
  };
  const members = page === 'PluginsView.vue'
    ? 'load,toggle,onToggle,plugins,changingIds,loading,scanning,requests'
    : page === 'PluginDetailView.vue' ? 'load,toggle,saveSettings,config,settingsLoaded,loading,requests,plugin,features,status,error'
    : 'loadStatusSettings,loadFeatureSettings,loadSettings,openSettings,saveSettings,statusSettings,featureSettings,settingsOpen,settingsConfig,requests,checkReadme,openReadme,closeReadme,readmeController,readmeAvailable,readmeOpen,readmeLoading,readmeHtml,readmeError';
  const scope = vue.effectScope();
  let subject;
  scope.run(() => { subject = evaluate('../../src/features/plugins/' + page, imports, members); });
  return { calls, toasts, renders, subject, route, safeModeEnabled, dispose() {
    if (disposed) return;
    disposed = true;
    unmount();
    scope.stop();
  } };
}
const response = (value, ok = true) => ({ ok, json: async () => value });

function trackedRequestFixture(page = 'PluginInfoView.vue') {
  const maps = [];
  const f = fixture(page, { Map: class extends Map {
    constructor(...args) { super(...args); maps.push(this); }
  } });
  assert.equal(maps.length, 1);
  return { ...f, controllers: maps[0] };
}


test('safe mode clears modal configuration and cancels late configuration reads', async () => {
  const f = fixture();
  try {
    f.subject.settingsConfig.value = { secret: 'private' };
    f.subject.settingsOpen.value = true;
    const loading = f.subject.loadSettings();
    f.safeModeEnabled.value = true;
    assert.equal(f.subject.settingsOpen.value, false);
    assert.equal(Object.keys(f.subject.settingsConfig.value).length, 0);
    for (const call of f.calls) call.resolve(response({ secret: 'late-private' }));
    await assert.rejects(loading, { name: 'AbortError' });
    assert.equal(Object.keys(f.subject.settingsConfig.value).length, 0);
  } finally { f.dispose(); }
});

test('safe mode clears plugin home configuration and reloads without accepting late private values', async () => {
  const f = fixture('PluginDetailView.vue');
  try {
    f.subject.config.secret = 'private';
    const first = f.subject.load();
    f.safeModeEnabled.value = true;
    assert.equal(Object.keys(f.subject.config).length, 0);
    f.calls[0].resolve(response({ id: 'first', enabled: false, state: 'Loaded', manifest: {} }));
    await first;
    f.calls[1].resolve(response({ id: 'first', enabled: false, state: 'Loaded', manifest: {} }));
    await setImmediate();
    for (const call of f.calls.slice(2)) call.resolve(response(
      call.url.endsWith('/configuration') ? { plain: 'visible', secret: '••••' }
        : call.url.endsWith('/configuration/schema') ? [{ name: 'secret', type: 'text', masked: true }]
          : { registerOnHome: true }));
    await setImmediate();
    assert.equal(f.subject.config.secret, '••••');
    assert.equal(f.subject.config.plain, 'visible');
    assert.equal(f.subject.error.value, '');
  } finally { f.dispose(); }
});

test('a cancelled readme availability check cannot hide the new page entry', async () => {
  const f = fixture();
  try {
    const first = f.subject.checkReadme('old');
    f.subject.requests.cancelAll();
    const second = f.subject.checkReadme('new');
    f.calls[1].resolve(response({}));
    await second;
    assert.equal(f.subject.readmeAvailable.value, true);
    f.calls[0].resolve(response({}, false));
    await first;
    assert.equal(f.subject.readmeAvailable.value, true);
  } finally { f.dispose(); }
});

test('readme loads only on opening, releases content on closing and reloads without caching', async () => {
  const f = fixture();
  try {
    assert.equal(f.calls.length, 0);
    f.subject.readmeAvailable.value = true;
    const first = f.subject.openReadme();
    await f.subject.openReadme();
    assert.equal(f.calls.length, 1);
    f.calls[0].resolve({ ok: true, text: async () => '# Document' });
    await setImmediate();
    f.renders[0].resolve('<h1>Document</h1>');
    await first;
    assert.equal(f.subject.readmeHtml.value, '<h1>Document</h1>');
    f.subject.closeReadme();
    assert.equal(f.subject.readmeOpen.value, false);
    assert.equal(f.subject.readmeHtml.value, '');
    const second = f.subject.openReadme();
    assert.equal(f.calls.length, 2);
    f.calls[1].resolve(response({}, false));
    await second;
    assert.equal(f.subject.readmeError.value, '读取说明文档失败');
    f.subject.closeReadme();
    assert.equal(f.subject.readmeError.value, '');
  } finally { f.dispose(); }
});

test('closing or leaving a readme cancels work and late rendering cannot overwrite a reopened dialog', async () => {
  const f = fixture();
  try {
    f.subject.readmeAvailable.value = true;
    const first = f.subject.openReadme();
    f.calls[0].resolve({ ok: true, text: async () => 'old' });
    await setImmediate();
    const dialogSignal = f.subject.readmeController.signal;
    f.subject.closeReadme();
    assert.equal(dialogSignal.aborted, true);
    assert.equal(f.calls[0].init.signal.aborted, false);
    const second = f.subject.openReadme();
    f.renders[0].resolve('<p>old</p>');
    await first;
    assert.equal(f.subject.readmeHtml.value, '');
    assert.equal(f.subject.readmeLoading.value, true);
    f.dispose();
    assert.equal(f.calls[1].init.signal.aborted, true);
    f.calls[1].reject(new Error('late failure'));
    await second;
    assert.equal(f.subject.readmeError.value, '');
    assert.equal(f.subject.readmeOpen.value, false);
  } finally { f.dispose(); }
});

test('plugin home retains loaded content during refresh and on failure without allowing a concurrent save', async () => {
  const f = fixture('PluginDetailView.vue');
  try {
    f.subject.plugin.value = { id: 'first', enabled: true, state: 'Running', manifest: {} };
    f.subject.status.state = 'ready';
    f.subject.features.value = [{ id: 'page' }];
    f.subject.settingsLoaded.value = true;
    const pending = f.subject.load();
    assert.equal(f.subject.status.state, 'ready');
    assert.equal(f.subject.features.value.length, 1);
    await f.subject.saveSettings();
    assert.equal(f.calls.length, 1);
    f.calls[0].reject(new Error('refresh failed'));
    await pending;
    assert.equal(f.subject.status.state, 'ready');
    assert.equal(f.subject.features.value.length, 1);
    assert.equal(f.subject.settingsLoaded.value, true);
    assert.equal(f.subject.error.value, 'refresh failed');
  } finally { f.dispose(); }
});

test('both configuration editors show backend validation details with a fallback for unreadable errors', async () => {
  for (const page of ['PluginDetailView.vue', 'PluginInfoView.vue']) {
    for (const body of [{ detail: '延迟秒数不能小于 1' }, {}, { detail: 1 }, { detail: ' ' }, null, undefined]) {
      const f = fixture(page);
      try {
        if (page === 'PluginDetailView.vue') f.subject.settingsLoaded.value = true;
        const saving = f.subject.saveSettings();
        f.calls[0].resolve(body === undefined
          ? { ok: false, json: async () => { throw new SyntaxError('invalid JSON'); } }
          : response(body, false));
        await saving;
        assert.equal(f.toasts.length, 1);
        assert.equal(f.toasts[0].type, 'error');
        assert.equal(f.toasts[0].text, typeof body?.detail === 'string' && body.detail.trim() ? body.detail : '配置保存失败，请重试');
        assert.equal(f.calls.length, 1);
      } finally { f.dispose(); }
    }
  }
});

test('closing either configuration editor while reading an error suppresses late toasts', async () => {
  for (const page of ['PluginDetailView.vue', 'PluginInfoView.vue']) {
    const f = fixture(page);
    if (page === 'PluginDetailView.vue') f.subject.settingsLoaded.value = true;
    let releaseBody;
    const saving = f.subject.saveSettings();
    f.calls[0].resolve({ ok: false, json: () => new Promise(resolve => { releaseBody = resolve; }) });
    await setImmediate();
    f.dispose();
    releaseBody({ detail: '延迟秒数不能小于 1' });
    await saving;
    assert.equal(f.toasts.length, 0);
    assert.equal(f.calls.length, 1);
  }
});

test('failed enabling restores the native checkbox before issuing the request', async () => {
  const f = fixture('PluginsView.vue');
  try {
    const plugin = { id: 'first', enabled: false, state: 'Disabled', canChangeEnabled: true };
    f.subject.plugins.value = [plugin];
    const input = { checked: true };
    const toggling = f.subject.onToggle({ target: input }, plugin);
    assert.equal(input.checked, false);
    f.calls[0].resolve(response({ ...plugin, state: 'Failed', canChangeEnabled: false, errors: ['校验失败'] }));
    await toggling;
    assert.equal(input.checked, false);
    assert.equal(f.subject.plugins.value[0].enabled, false);
    assert.equal(f.toasts[0].text, '插件启用失败');
  } finally { f.dispose(); }
});

test('plugin home toggle reports its operation result rather than the reloaded state', async () => {
  const f = fixture('PluginDetailView.vue');
  try {
    f.subject.plugin.value = { id: 'first', enabled: false, state: 'Disabled', canChangeEnabled: true };
    const toggling = f.subject.toggle();
    f.calls[0].resolve(response({ id: 'first', enabled: false, state: 'Failed', canChangeEnabled: false, errors: ['需要 WinPilot 2.0.0 或更高版本'] }));
    await setImmediate();
    f.calls[1].resolve(response({ id: 'first', enabled: false, state: 'Disabled', manifest: {} }));
    await setImmediate();
    for (const call of f.calls.slice(2))
      call.resolve(response(call.url.endsWith('/configuration/schema') ? [] : {}));
    await toggling;
    assert.equal(f.toasts.length, 1);
    assert.equal(f.toasts[0].text, '插件启用失败');
    assert.equal(f.toasts[0].type, 'error');
  } finally { f.dispose(); }
});

test('leaving either plugin view suppresses late toggle results and toasts', async () => {
  for (const page of ['PluginsView.vue', 'PluginDetailView.vue']) {
    const f = fixture(page);
    const plugin = { id: 'first', enabled: false, state: 'Disabled', canChangeEnabled: true };
    if (page === 'PluginDetailView.vue') f.subject.plugin.value = plugin;
    else f.subject.plugins.value = [plugin];
    const toggling = f.subject.toggle(plugin);
    f.dispose();
    f.calls[0].resolve(response({ ...plugin, enabled: true, state: 'Running' }));
    await toggling;
    assert.equal(f.toasts.length, 0);
    assert.equal(f.calls.length, 1);
  }
});

test('details wait for lifecycle completion before loading status and features', async () => {
  const f = fixture('PluginDetailView.vue');
  try {
    const loading = f.subject.load();
    assert.equal(f.calls.length, 1);
    assert.equal(f.calls[0].url, '/api/plugins/first?waitForLifecycle=true');
    assert.equal(f.calls[0].init.cache, 'no-store');
    assert.equal(f.subject.loading.value, true);
    await setImmediate();
    assert.equal(f.calls.length, 1);
    f.calls[0].resolve(response({ id: 'first', enabled: true, state: 'Running', manifest: { status_ui: true, features_ui: true } }));
    await setImmediate();
    for (const call of f.calls.slice(1)) {
      const body = call.url.endsWith('/features') ? { features: [{ id: 'page' }] }
        : call.url.endsWith('/status') ? { ready: true }
        : call.url.endsWith('/configuration/schema') ? [] : {};
      call.resolve(response(body));
    }
    await loading;
    assert.equal(f.subject.plugin.value.state, 'Running');
    assert.equal(f.subject.features.value[0].id, 'page');
    assert.equal(f.subject.status.ready, true);
    assert.equal(f.calls.length, 6);
  } finally { f.dispose(); }
});

test('leaving during lifecycle waiting cancels the query and ignores its late response', async () => {
  const f = fixture('PluginDetailView.vue');
  const loading = f.subject.load();
  const call = f.calls[0];
  f.dispose();
  assert.equal(call.init.signal.aborted, true);
  call.resolve(response({ id: 'first', enabled: true, state: 'Running', manifest: {} }));
  await loading;
  assert.equal(f.subject.plugin.value, undefined);
  assert.equal(f.calls.length, 1);
  assert.equal(f.toasts.length, 0);
});

test('failed lifecycle query stops loading without issuing more requests', async () => {
  const f = fixture('PluginDetailView.vue');
  try {
    const loading = f.subject.load();
    f.calls[0].resolve(response({}, false));
    await loading;
    assert.equal(f.subject.loading.value, false);
    assert.equal(f.subject.error.value, '无法加载插件');
    assert.equal(f.calls.length, 1);
  } finally { f.dispose(); }
});

test('failed and disabled plugins do not request runtime status or features', async () => {
  for (const plugin of [{ enabled: true, state: 'Failed' }, { enabled: false, state: 'Disabled' }]) {
    const f = fixture('PluginDetailView.vue');
    try {
      const loading = f.subject.load();
      f.calls[0].resolve(response({ id: 'first', ...plugin, manifest: { status_ui: true, features_ui: true } }));
      await setImmediate();
      assert.equal(f.calls.length, 4);
      f.calls[1].resolve(response({}));
      f.calls[2].resolve(response([]));
      f.calls[3].resolve(response({ registerOnHome: true }));
      await loading;
      assert.equal(f.subject.plugin.value.state, plugin.state);
      assert.equal(f.subject.settingsLoaded.value, true);
      assert.equal(f.subject.features.value.length, 0);
      assert.equal(Object.keys(f.subject.status).length, 0);
    } finally { f.dispose(); }
  }
});

test('saved empty status and feature selections stay empty', async () => {
  const f = fixture();
  try {
    const status = f.subject.loadStatusSettings();
    f.calls[0].resolve(response({ value: 1 }));
    f.calls[1].resolve(response({ statusKeys: [], registerOnHome: true }));
    await status;
    assert.equal(f.subject.statusSettings.value.statusKeys.length, 0);
    const features = f.subject.loadFeatureSettings();
    f.calls[2].resolve(response({ features: [{ id: 'page' }] }));
    f.calls[3].resolve(response({ featureIds: [], registerOnHome: true }));
    await features;
    assert.equal(f.subject.featureSettings.value.featureIds.length, 0);
  } finally { f.dispose(); }
});

test('failed configuration reading keeps saved data and does not open an editor', async () => {
  const f = fixture();
  try {
    f.subject.settingsConfig.value = { important: 'saved' };
    const opening = f.subject.openSettings();
    f.calls[0].resolve(response({ registerOnHome: true }));
    f.calls[1].resolve(response([{ name: 'important', type: 'text', default: '' }]));
    f.calls[2].resolve(response({}, false));
    await opening;
    assert.equal(f.subject.settingsOpen.value, false);
    assert.equal(f.subject.settingsConfig.value.important, 'saved');
    assert.equal(f.toasts.length, 1);
  } finally { f.dispose(); }
});

test('old list response bodies cannot replace a newer scan', async () => {
  const f = fixture('PluginsView.vue');
  try {
    let releaseBody;
    const first = f.subject.load();
    f.calls[0].resolve({ ok: true, json: () => new Promise(resolve => { releaseBody = resolve; }) });
    await setImmediate();
    const second = f.subject.load();
    f.calls[1].resolve(response([{ id: 'new' }]));
    await second;
    releaseBody([{ id: 'old' }]);
    await first;
    assert.equal(f.subject.plugins.value[0].id, 'new');
    assert.equal(f.subject.loading.value, false);
  } finally { f.dispose(); }
});

test('different plugin toggles keep independent pending flags and prevent duplicate writes', async () => {
  const f = fixture('PluginsView.vue');
  try {
    const a = { id: 'a', enabled: false, canChangeEnabled: true };
    const b = { id: 'b', enabled: false, canChangeEnabled: true };
    const first = f.subject.toggle(a);
    const second = f.subject.toggle(b);
    await f.subject.toggle(a);
    assert.equal(f.calls.length, 2);
    f.calls[0].resolve(response({ ...a, enabled: true }));
    await first;
    assert.equal(f.subject.changingIds.value.has('b'), true);
    f.calls[1].resolve(response({ ...b, enabled: true }));
    await second;
    assert.equal(f.subject.changingIds.value.size, 0);
  } finally { f.dispose(); }
});

test('an old rescan cannot replace a newer read and toggles wait for scans', async () => {
  const f = fixture('PluginsView.vue');
  try {
    let releaseBody;
    const first = f.subject.load(true);
    assert.equal(f.subject.scanning.value, true);
    await f.subject.toggle({ id: 'a', enabled: false, canChangeEnabled: true });
    assert.equal(f.calls.length, 1);
    f.calls[0].resolve({ ok: true, json: () => new Promise(resolve => { releaseBody = resolve; }) });
    await setImmediate();
    const second = f.subject.load();
    assert.equal(f.subject.scanning.value, false);
    f.calls[1].resolve(response([{ id: 'new' }]));
    await second;
    assert.equal(f.subject.scanning.value, false);
    releaseBody([{ id: 'old' }]);
    await first;
    assert.equal(f.subject.plugins.value[0].id, 'new');
  } finally { f.dispose(); }
});

test('leaving cancels writes and late bodies cannot update data or send a toast', async () => {
  const f = fixture('PluginsView.vue');
  let releaseBody;
  const pending = f.subject.toggle({ id: 'a', enabled: false, canChangeEnabled: true });
  f.calls[0].resolve({ ok: true, json: () => new Promise(resolve => { releaseBody = resolve; }) });
  await setImmediate();
  f.dispose();
  releaseBody({ id: 'a', enabled: true });
  await pending;
  assert.equal(f.calls[0].init.signal.aborted, true);
  assert.equal(f.toasts.length, 0);
  assert.equal(f.subject.plugins.value.length, 0);
});

test('shared settings predicates validate collections without plugin business rules', () => {
  const models = evaluate('../../src/features/plugins/PluginModels.ts', () => ({}));
  assert.equal(models.isPluginSetting({ name: 'value', type: 'text' }), true);
  assert.equal(models.isPluginSetting({ name: '', type: 'text' }), false);
  assert.equal(models.isPluginCollection({ name: 'items', type: 'collection', mode: 'dynamic' }), true);
  assert.equal(models.isPluginCollection({ name: 'items', type: 'collection', mode: 'unknown' }), false);
});

test('details refuse saving when configuration loading failed', async () => {
  const f = fixture('PluginDetailView.vue');
  try {
    f.subject.config.important = 'saved';
    const loading = f.subject.load();
    f.calls[0].resolve(response({ id: 'first', enabled: false, manifest: {} }));
    await setImmediate();
    f.calls[1].resolve(response({}, false));
    f.calls[2].resolve(response([]));
    f.calls[3].resolve(response({ registerOnHome: true }));
    await loading;
    await f.subject.saveSettings();
    assert.equal(f.calls.length, 4);
    assert.equal(f.subject.settingsLoaded.value, false);
    assert.equal(f.subject.config.important, 'saved');
  } finally { f.dispose(); }
});

test('late detail configuration bodies cannot erase a newer loaded configuration', async () => {
  const f = fixture('PluginDetailView.vue');
  try {
    let releaseBody;
    const first = f.subject.load();
    f.calls[0].resolve(response({ id: 'first', enabled: false, manifest: {} }));
    await setImmediate();
    f.calls[1].resolve({ ok: true, json: () => new Promise(resolve => { releaseBody = resolve; }) });
    f.calls[2].resolve(response([]));
    f.calls[3].resolve(response({ registerOnHome: true }));
    await setImmediate();
    const second = f.subject.load();
    f.calls[4].resolve(response({ id: 'first', enabled: false, manifest: {} }));
    await setImmediate();
    f.calls[5].resolve(response({ important: 'new' }));
    f.calls[6].resolve(response([]));
    f.calls[7].resolve(response({ registerOnHome: true }));
    await second;
    releaseBody({ important: 'old' });
    await first;
    assert.equal(f.subject.config.important, 'new');
    assert.equal(f.subject.settingsLoaded.value, true);
  } finally { f.dispose(); }
});

test('saving display settings cannot proceed after failed configuration save', async () => {
  const f = fixture();
  try {
    const saving = f.subject.saveSettings();
    f.calls[0].resolve(response({}, false));
    await saving;
    assert.equal(f.calls.length, 1);
    assert.match(f.calls[0].url, /\/configuration$/);
    assert.equal(f.toasts.length, 1);
  } finally { f.dispose(); }
});

test('plugin HEAD checks finish tracking without reading a body', async () => {
  const f = trackedRequestFixture();
  let bodyReads = 0;
  try {
    const checking = f.subject.checkReadme('first');
    assert.equal(f.calls[0].init.method, 'HEAD');
    f.calls[0].resolve({ ok: true, status: 200, body: null, json: async () => { bodyReads++; return {}; } });
    await checking;
    assert.equal(f.subject.readmeAvailable.value, true);
    assert.equal(bodyReads, 0);
    assert.equal(f.controllers.size, 0);
  } finally { f.dispose(); }
});

test('status-only plugin saves release both requests without requiring JSON', async () => {
  const f = trackedRequestFixture();
  let saving;
  try {
    saving = f.subject.saveSettings();
    f.calls[0].resolve(new Response('not JSON', { status: 200 }));
    await setImmediate();
    assert.equal(f.calls.length, 2);
    assert.ok(f.calls.every(call => call.init.method === 'PUT'));
    f.calls[1].resolve(new Response('not JSON', { status: 200 }));
    await saving;
    assert.equal(f.toasts.length, 1);
    assert.equal(f.toasts[0].text, '配置展示设置已保存');
    assert.equal(f.toasts[0].type, 'success');
    assert.equal(f.controllers.size, 0);
  } finally {
    f.dispose();
    for (const call of f.calls) call.resolve(response({}));
    if (saving) await saving;
  }
});

test('plugin HTTP failures before body reading release their request', async () => {
  const f = trackedRequestFixture('PluginDetailView.vue');
  try {
    const loading = f.subject.load();
    f.calls[0].resolve(new Response('not JSON', { status: 503 }));
    await loading;
    assert.equal(f.subject.error.value, '无法加载插件');
    assert.equal(f.subject.loading.value, false);
    assert.equal(f.calls.length, 1);
    assert.equal(f.controllers.size, 0);
  } finally { f.dispose(); }
});
