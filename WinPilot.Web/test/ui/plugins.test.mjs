import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';
import { combinePageSignal } from '../../src/features/plugins/page/PluginPageLifetime.ts';

function evaluate(path, require, members = '') {
  const code = loadScriptModule(new URL(path, import.meta.url), members);
  const context = { exports: {}, require, AbortController, DOMException, console, sessionStorage: { getItem: () => null, removeItem() {} }, EventSource: class { close() {} } };
  runInNewContext(code, context);
  return context.exports;
}

function fixture(page = 'PluginInfoView.vue') {
  const calls = [], toasts = [];
  const route = vue.reactive({ params: { pluginId: 'first' }, fullPath: '/plugins/first' });
  let unmount;
  const client = {
    apiFetch: (url, init) => new Promise((resolve, reject) => calls.push({ url, init, resolve, reject })),
    isAbortError: error => error?.name === 'AbortError',
    userErrorMessage: (error, fallback) => error.message || fallback,
  };
  const requests = evaluate('../../src/features/plugins/PluginRequests.ts', name => name.endsWith('/client') ? client : { combinePageSignal });
  const models = evaluate('../../src/features/plugins/PluginModels.ts', () => ({}));
  const imports = name => {
    if (name === 'vue') return { ...vue, onMounted() {}, onBeforeUnmount: callback => { unmount = callback; } };
    if (name === 'vue-router') return { useRoute: () => route, useRouter: () => ({ push: async () => {} }) };
    if (name.endsWith('/PluginRequests')) return requests;
    if (name.endsWith('/PluginModels')) return models;
    if (name.endsWith('/client')) return client;
    if (name.endsWith('/toast')) return { showToast: (text, type) => toasts.push({ text, type }) };
    if (name.endsWith('/safeMode')) return { safeModeEnabled: vue.ref(false) };
    return {};
  };
  const members = page === 'PluginsView.vue'
    ? 'load,toggle,plugins,changingIds,loading,requests'
    : page === 'PluginDetailView.vue' ? 'load,saveSettings,config,settingsLoaded,loading,requests,plugin,features,status,error'
    : 'loadStatusSettings,loadFeatureSettings,openSettings,saveSettings,statusSettings,featureSettings,settingsOpen,settingsConfig,requests';
  const scope = vue.effectScope();
  let subject;
  scope.run(() => { subject = evaluate('../../src/features/plugins/' + page, imports, members); });
  return { calls, toasts, subject, route, dispose() { unmount(); scope.stop(); } };
}
const response = (value, ok = true) => ({ ok, json: async () => value });

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
      const body = call.url.endsWith('/features-definition') ? { features: [{ id: 'page' }] }
        : call.url.endsWith('/status') ? { ready: true }
        : call.url.endsWith('/settings-definition') ? [] : {};
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
    await f.subject.toggle({ id: 'a', enabled: false, canChangeEnabled: true });
    assert.equal(f.calls.length, 1);
    f.calls[0].resolve({ ok: true, json: () => new Promise(resolve => { releaseBody = resolve; }) });
    await setImmediate();
    const second = f.subject.load();
    f.calls[1].resolve(response([{ id: 'new' }]));
    await second;
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

test('cancelled late network errors are treated as cancellation', async () => {
  const f = fixture();
  try {
    const pending = f.subject.requests.fetch('/test');
    f.subject.requests.cancelAll();
    f.calls[0].reject(new Error('late network error'));
    await assert.rejects(pending, error => error.name === 'AbortError');
  } finally { f.dispose(); }
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
    assert.match(f.calls[0].url, /\/storage$/);
    assert.equal(f.toasts.length, 1);
  } finally { f.dispose(); }
});
