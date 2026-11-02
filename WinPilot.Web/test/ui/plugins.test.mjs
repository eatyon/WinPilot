import assert from 'node:assert/strict';
import * as delayedLoading from '../../src/ui/useDelayedLoading.ts';
import { modalStateModule } from '../support/modalState.mjs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';
import * as apiErrors from '../support/apiErrors.mjs';
import { combinePageSignal } from '../../src/features/plugins/page/pluginPageLifetime.ts';

function evaluate(path, require, members = '', globals = {}) {
  const code = loadScriptModule(new URL(path, import.meta.url), members);
  const context = { exports: {}, require: name => name.endsWith('/useDelayedLoading') ? delayedLoading : require(name), AbortController, DOMException, console, sessionStorage: { getItem: () => null, removeItem() {} }, EventSource: class { close() {} }, ...globals };
  runInNewContext(code, context);
  return context.exports;
}

function fixture(page = 'PluginInfoView.vue', requestGlobals = {}) {
  const calls = [], toasts = [], renders = [];
  const safeModeEnabled = vue.ref(false);
  const route = vue.reactive({ params: { pluginId: 'first' }, query: {}, fullPath: '/plugins/first' });
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
  const requests = evaluate('../../src/features/plugins/pluginRequests.ts', () => executor, '', requestGlobals);
  const models = evaluate('../../src/features/plugins/pluginModels.ts', () => ({}));
  const configuration = evaluate('../../src/features/plugins/pluginConfiguration.ts', () => models);
  const imports = name => {
    if (name.endsWith('/useModalState')) return modalStateModule;
    if (name === 'vue') return { ...vue, useId: () => 'plugin-test', onMounted() {}, onBeforeUnmount: callback => { unmount = callback; } };
    if (name === 'vue-router') return { useRoute: () => route, useRouter: () => ({ push: async () => {} }) };
    if (name.endsWith('/pluginRequests')) return requests;
    if (name.endsWith('/pluginModels')) return models;
    if (name.endsWith('/pluginConfiguration')) return configuration;
    if (name.endsWith('/client')) return client;
    if (name.endsWith('/toast')) return { showToast: (text, type) => toasts.push({ text, type }) };
    if (name.endsWith('/markdown')) return { renderMarkdown: source => new Promise((resolve, reject) => renders.push({ source, resolve, reject })) };
    if (name.endsWith('/theme')) return { useTheme: () => ({ accentTokens: vue.ref({ text: '#0067c0' }) }) };
    if (name.endsWith('/safeMode')) return { safeModeEnabled };
    return {};
  };
  const members = page === 'PluginsView.vue'
    ? 'load,toggle,plugins,filteredPlugins,search,changingIds,loading,scanning,requests'
    : page === 'PluginDetailView.vue' ? 'load,retryLoad,toggle,saveSettings,executeFeature,beginFeature,confirmingFeatureModal,config,settingsLoaded,loading,hasLoaded,requests,plugin,features,status,error,sections,settingsDisplay'
    : 'load,plugin,loadStatusSettings,loadFeatureSettings,loadSettings,openSettings,openStatusSettings,openFeatureSettings,loadingCapability,statusSettingsOpenModal,featureSettingsOpenModal,settingsOpenModal,saveSettings,statusSettings,featureSettings,settingsOpen,settingsConfig,requests,readmeOpenModal,openReadme,closeReadme,readmeController,readmeAvailable,readmeOpen,readmeLoading,readmeHtml,deletePlugin,deleting,deletingData';
  const scope = vue.effectScope();
  let subject;
  scope.run(() => { subject = evaluate('../../src/features/plugins/' + page, imports, page === 'PluginInfoView.vue' ? `${members},pageElement,highlightedCapability` : members); });
  return { calls, toasts, renders, subject, route, safeModeEnabled, dispose() {
    if (disposed) return;
    disposed = true;
    unmount();
    scope.stop();
  } };
}
const response = (value, ok = true) => ({ ok, json: async () => value });

test('plugin section retry preserves configuration drafts and skips unrelated reads', async () => {
  for (const section of ['status', 'features']) {
    const f = fixture('PluginDetailView.vue');
    try {
      f.subject.config.sample = 'unsaved';
      f.subject.sections[section].error = 'failed';
      const retry = f.subject.retryLoad(section);
      f.calls[0].resolve(response({ id: 'first', enabled: true, state: 'Running', manifest: { status_ui: true, features_ui: true, settings_ui: true } }));
      await setImmediate();
      assert.equal(f.calls.length, 2);
      assert.ok(f.calls[1].url.endsWith(`/${section}`));
      f.calls[1].resolve(response(section === 'features' ? { features: [] } : {}));
      await retry;
      assert.equal(f.subject.config.sample, 'unsaved');
      assert.equal(f.subject.sections[section].error, '');
    } finally { f.dispose(); }
  }
});

test('plugin saving, lifecycle changes and action execution exclude competing writes and retries', async () => {
  for (const operation of ['save', 'toggle', 'execute']) {
    const f = fixture('PluginDetailView.vue');
    try {
      f.subject.plugin.value = { id: 'first', enabled: true, canChangeEnabled: true };
      f.subject.settingsLoaded.value = true;
      const feature = { id: 'action', mode: 'action', confirm: true };
      const operations = {
        save: () => f.subject.saveSettings(),
        toggle: () => f.subject.toggle(),
        execute: () => f.subject.executeFeature(feature),
      };
      const pending = operations[operation]();
      await f.subject.saveSettings();
      await f.subject.toggle();
      await f.subject.executeFeature(feature);
      await f.subject.retryLoad('status');
      f.subject.beginFeature(feature);
      assert.equal(f.calls.length, 1);
      assert.equal(f.subject.confirmingFeatureModal.show, false);
      f.calls[0].resolve(response({}, false));
      await pending;
      f.subject.beginFeature(feature);
      assert.equal(f.subject.confirmingFeatureModal.show, true);
    } finally { f.dispose(); }
  }
});

test('plugin action entries stay mutually exclusive and recover after execution failure', async () => {
  const f = fixture('PluginDetailView.vue');
  try {
    const pending = f.subject.executeFeature({ id: 'first', mode: 'action' });
    f.subject.beginFeature({ id: 'second', mode: 'action', confirm: true });
    f.subject.beginFeature({ id: 'third', mode: 'action', confirm: false });
    assert.equal(f.calls.length, 1);
    assert.equal(f.subject.confirmingFeatureModal.show, false);
    f.calls[0].resolve(response({}, false));
    await pending;
    f.subject.beginFeature({ id: 'second', mode: 'action', confirm: true });
    assert.equal(f.subject.confirmingFeatureModal.show, true);
  } finally { f.dispose(); }
});

for (const [key, open, modal, count] of [
  ['status', 'openStatusSettings', 'statusSettingsOpenModal', 2],
  ['features', 'openFeatureSettings', 'featureSettingsOpenModal', 2],
  ['settings', 'openSettings', 'settingsOpenModal', 3],
]) {
  test(`${key} capability opening rejects repeated and competing clicks and recovers after failure`, async () => {
    const f = fixture();
    try {
      const first = f.subject[open]();
      await f.subject[open]();
      await f.subject[key === 'status' ? 'openFeatureSettings' : 'openStatusSettings']();
      assert.equal(f.calls.length, count);
      assert.equal(f.subject.loadingCapability.value, key);
      assert.equal(f.subject[modal].show, false);
      f.calls[0].resolve(response({}, false));
      for (const call of f.calls.slice(1)) call.resolve(response({}));
      await first;
      assert.equal(f.subject.loadingCapability.value, '');
      assert.equal(f.subject[modal].show, false);
      assert.equal(f.toasts.length, 1);
      const retry = f.subject[open]();
      for (const call of f.calls.slice(count))
        call.resolve(response(call.url.endsWith('/schema') ? [] : {}));
      await retry;
      assert.equal(f.calls.length, count * 2);
      assert.equal(f.subject[modal].show, true);
      assert.equal(f.subject.loadingCapability.value, '');
    } finally { f.dispose(); }
  });
}

test('capability preparation cannot open a dialog after safe mode changes or unmount', async () => {
  for (const leave of [false, true]) {
    const f = fixture();
    try {
      const pending = f.subject.openStatusSettings();
      if (leave) f.dispose();
      else {
        f.safeModeEnabled.value = true;
        f.safeModeEnabled.value = false;
      }
      for (const call of f.calls) call.resolve(response({}));
      await pending;
      assert.equal(f.subject.statusSettingsOpenModal.show, false);
      assert.equal(f.toasts.length, 0);
    } finally { f.dispose(); }
  }
});

test('plugin info loads metadata only until a capability editor is opened', async () => {
  const f = fixture();
  try {
    const loading = f.subject.load();
    f.calls[0].resolve(response({ id: 'first', manifest: { status_ui: true, features_ui: true, settings_ui: true } }));
    await loading;
    assert.equal(f.calls.length, 1);
    const opening = f.subject.openSettings();
    assert.deepEqual(f.calls.slice(1).map(call => call.url), [
      '/api/plugins/first/settings/display/configuration',
      '/api/plugins/first/configuration/schema',
      '/api/plugins/first/configuration',
    ]);
    f.calls[1].resolve(response({ registerOnHome: true }));
    f.calls[2].resolve(response([]));
    f.calls[3].resolve(response({}));
    await opening;
    assert.equal(f.subject.settingsOpen.value, true);
  } finally { f.dispose(); }
});

test('plugin home waits for all sections before revealing content and keeps section errors independent', async () => {
  const f = fixture('PluginDetailView.vue');
  try {
    const loading = f.subject.load();
    f.calls[0].resolve(response({ id: 'first', enabled: true, state: 'Running', manifest: { status_ui: true, features_ui: true } }));
    await setImmediate();
    assert.equal(f.subject.plugin.value.id, 'first');
    assert.equal(f.subject.hasLoaded.value, false);
    assert.equal(f.subject.loading.value, true);
    assert.equal(f.subject.sections.status.loading, true);
    f.calls.find(call => call.url.endsWith('/features')).resolve(response({ features: [{ id: 'page' }] }));
    await setImmediate();
    assert.equal(f.subject.features.value[0].id, 'page');
    assert.equal(f.subject.sections.features.loading, false);
    assert.equal(f.subject.sections.status.loading, true);
    assert.equal(f.subject.hasLoaded.value, false);
    f.calls.find(call => call.url.endsWith('/status')).resolve(response({}, false));
    await loading;
    assert.equal(f.subject.hasLoaded.value, true);
    assert.equal(f.subject.sections.status.error, '无法加载状态');
    assert.equal(f.subject.sections.features.error, '');
    assert.equal(f.subject.error.value, '');
  } finally { f.dispose(); }
});

test('plugin home gates configuration reads on display settings, including failure and cancellation', async () => {
  for (const mode of ['hidden', 'visible', 'failed', 'disposed']) {
    const f = fixture('PluginDetailView.vue');
    try {
      const loading = f.subject.load();
      f.calls[0].resolve(response({ id: 'first', manifest: { settings_ui: true } }));
      await setImmediate();
      assert.equal(f.calls.length, 2);
      assert.ok(f.calls[1].url.endsWith('/settings/display/configuration'));
      assert.equal(f.subject.settingsDisplay.value.registerOnHome, false);
      if (mode === 'disposed') f.dispose();
      f.calls[1].resolve(response({ registerOnHome: mode !== 'hidden' }, mode !== 'failed'));
      await setImmediate();
      if (mode === 'visible') {
        assert.equal(f.calls.length, 4);
        assert.equal(f.subject.settingsDisplay.value.registerOnHome, true);
        f.calls.find(call => call.url === '/api/plugins/first/configuration').resolve(response({ sample: 'value' }));
        f.calls.find(call => call.url.endsWith('/schema')).resolve(response([{ name: 'sample', type: 'text' }]));
      }
      await loading;
      assert.equal(f.calls.length, mode === 'visible' ? 4 : 2);
      if (mode === 'failed') assert.equal(f.subject.sections.settings.error, '读取插件设置失败');
      if (mode === 'visible') assert.equal(f.subject.config.sample, 'value');
      else assert.equal(Object.keys(f.subject.config).length, 0);
    } finally { f.dispose(); }
  }
});

test('plugin setting options preserve schema values and dynamic empty results', () => {
  const props = vue.reactive({ field: { name: 'target', options: ['plain', { value: 'id', label: '名称' }] }, modelValue: [] });
  const scope = vue.effectScope();
  try {
    const subject = scope.run(() => evaluate('../../src/features/plugins/PluginSettingField.vue', name => {
      if (name === 'vue') return { ...vue, onBeforeUnmount() {} };
      if (name.endsWith('/useModalState')) return modalStateModule;
      if (name.endsWith('/pluginRequests')) return { createPluginRequests: () => ({ dispose() {} }) };
      return {};
    }, 'multiOptions,selectOptions,loadedOptions', { defineProps: () => props, defineEmits: () => () => {} }));
    const expected = [{ value: 'plain', label: 'plain' }, { value: 'id', label: '名称' }];
    assert.equal(JSON.stringify(subject.multiOptions.value), JSON.stringify(expected));
    assert.equal(JSON.stringify(subject.selectOptions.value), JSON.stringify(expected));
    subject.loadedOptions.value = [];
    assert.equal(subject.selectOptions.value.length, 0);
    assert.equal(subject.multiOptions.value.length, 2);
    subject.loadedOptions.value = [{ value: 'dynamic', label: '动态' }];
    assert.equal(subject.selectOptions.value[0].value, 'dynamic');
    assert.equal(props.field.options[1].value, 'id');
    subject.loadedOptions.value = undefined;
    props.field.options = undefined;
    assert.equal(subject.selectOptions.value.length, 0);
    assert.equal(subject.multiOptions.value.length, 0);
  } finally { scope.stop(); }
});

test('plugin search count follows matches and returns to the total when cleared', () => {
  const f = fixture('PluginsView.vue');
  try {
    f.subject.plugins.value = [
      { id: 'cleaner', manifest: { name: '内存清理', authors: [{ name: 'Alice' }] } },
      { id: 'monitor', manifest: { name: '状态监控', authors: [{ name: 'Bob' }] } },
    ];
    assert.equal(f.subject.filteredPlugins.value.length, 2);
    for (const query of ['内存', ' CLEANER ', 'alice']) {
      f.subject.search.value = query;
      assert.equal(f.subject.filteredPlugins.value.length, 1);
    }
    f.subject.search.value = '不存在';
    assert.equal(f.subject.filteredPlugins.value.length, 0);
    f.subject.search.value = '';
    assert.equal(f.subject.filteredPlugins.value.length, 2);
  } finally { f.dispose(); }
});

test('home management links focus only the requested available capability after loading', async () => {
  const f = fixture();
  const calls = [];
  try {
    f.subject.pageElement.value = {
      querySelector: selector => ({
        scrollIntoView: options => calls.push(['scroll', selector, options.block]),
        focus: options => calls.push(['focus', selector, options.preventScroll]),
      }),
    };
    f.route.query.focus = 'status';
    await vue.nextTick();
    assert.equal(calls.length, 0);
    f.subject.plugin.value = { manifest: { status_ui: true, features_ui: true } };
    await vue.nextTick();
    assert.deepEqual(calls.map(call => call.slice(0, 2)), [
      ['scroll', '[data-capability="status"]'], ['focus', '[data-capability="status"]'],
    ]);
    assert.equal(calls[1][2], true);
    assert.equal(f.subject.highlightedCapability.value, 'status');
    calls.length = 0;
    f.route.query.focus = 'features';
    await vue.nextTick();
    assert.equal(calls[1][1], '[data-capability="features"]');
    assert.equal(f.subject.highlightedCapability.value, 'features');
    calls.length = 0;
    f.route.query.focus = 'unknown';
    await vue.nextTick();
    f.subject.plugin.value = { manifest: { status_ui: false } };
    f.route.query.focus = 'status';
    await vue.nextTick();
    assert.equal(calls.length, 0);
    assert.equal(f.subject.highlightedCapability.value, '');
    f.safeModeEnabled.value = true;
    f.subject.plugin.value = { manifest: { status_ui: true } };
    await vue.nextTick();
    assert.equal(calls.length, 0);
  } finally { f.dispose(); }
});

function trackedRequestFixture(page = 'PluginInfoView.vue') {
  const maps = [];
  const f = fixture(page, { Map: class extends Map {
    constructor(...args) { super(...args); maps.push(this); }
  } });
  assert.equal(maps.length, 1);
  return { ...f, controllers: maps[0] };
}


test('plugin deletion modes share exclusion and clear their identity after success or failure', async () => {
  for (const deleteData of [false, true]) {
    for (const succeeds of [false, true]) {
      const f = fixture();
      try {
        const deleting = f.subject.deletePlugin(deleteData);
        assert.equal(f.subject.deleting.value, true);
        assert.equal(f.subject.deletingData.value, deleteData);
        await f.subject.deletePlugin(!deleteData);
        assert.equal(f.calls.length, 1);
        assert.ok(f.calls[0].url.endsWith(`deleteData=${deleteData}`));
        f.calls[0].resolve(response({}, succeeds));
        await deleting;
        assert.equal(f.subject.deleting.value, false);
        assert.equal(f.subject.deletingData.value, undefined);
      } finally { f.dispose(); }
    }
  }
});

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
    f.calls[1].resolve(response({ id: 'first', enabled: false, state: 'Loaded', manifest: { settings_ui: true } }));
    await setImmediate();
    f.calls[2].resolve(response({ registerOnHome: true }));
    await setImmediate();
    for (const call of f.calls.slice(3)) call.resolve(response(
      call.url.endsWith('/configuration') ? { plain: 'visible', secret: '••••' }
        : call.url.endsWith('/configuration/schema') ? [{ name: 'secret', type: 'text', masked: true }]
          : { registerOnHome: true }));
    await setImmediate();
    assert.equal(f.subject.config.secret, '••••');
    assert.equal(f.subject.config.plain, 'visible');
    assert.equal(f.subject.error.value, '');
  } finally { f.dispose(); }
});

test('a cancelled plugin detail read cannot hide the new readme entry', async () => {
  const f = fixture();
  try {
    const first = f.subject.load();
    f.subject.requests.cancelAll();
    const second = f.subject.load();
    f.calls[1].resolve(response({ id: 'new', hasReadme: true }));
    await second;
    assert.equal(f.subject.readmeAvailable.value, true);
    f.calls[0].resolve(response({ id: 'old', hasReadme: false }));
    await first;
    assert.equal(f.subject.readmeAvailable.value, true);
  } finally { f.dispose(); }
});

test('readme loads only on opening, releases content on closing and reloads without caching', async () => {
  const f = fixture();
  try {
    assert.equal(f.calls.length, 0);
    f.subject.plugin.value = { hasReadme: true };
    const first = f.subject.openReadme();
    await f.subject.openReadme();
    assert.equal(f.subject.readmeOpenModal.show, false);
    assert.equal(f.calls.length, 1);
    f.calls[0].resolve({ ok: true, text: async () => '# Document' });
    await setImmediate();
    f.renders[0].resolve('<h1>Document</h1>');
    await first;
    assert.equal(f.subject.readmeHtml.value, '<h1>Document</h1>');
    f.subject.closeReadme();
    assert.equal(f.subject.readmeOpenModal.show, false);
    assert.equal(f.subject.readmeHtml.value, '<h1>Document</h1>');
    f.subject.readmeOpenModal.afterLeave();
    assert.equal(f.subject.readmeOpen.value, false);
    assert.equal(f.subject.readmeHtml.value, '');
    const second = f.subject.openReadme();
    assert.equal(f.calls.length, 2);
    f.calls[1].resolve(response({}, false));
    await second;
    assert.equal(f.subject.readmeOpenModal.show, false);
    assert.equal(f.toasts.at(-1).text, '读取说明文档失败');
    f.subject.closeReadme();
    assert.equal(f.subject.readmeOpenModal.show, false);
    f.subject.readmeOpenModal.afterLeave();
  } finally { f.dispose(); }
});

test('closing or leaving a readme cancels work and late rendering cannot overwrite a reopened dialog', async () => {
  const f = fixture();
  try {
    f.subject.plugin.value = { hasReadme: true };
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
    f.subject.hasLoaded.value = true;
    const pending = f.subject.load();
    assert.equal(f.subject.hasLoaded.value, true);
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
    assert.equal(f.subject.hasLoaded.value, true);
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

test('controlled plugin switch keeps its value until a successful response', async () => {
  const f = fixture('PluginsView.vue');
  try {
    const plugin = { id: 'first', enabled: false, state: 'Disabled', canChangeEnabled: true };
    f.subject.plugins.value = [plugin];
    const toggling = f.subject.toggle(plugin);
    assert.equal(f.subject.plugins.value[0].enabled, false);
    assert.equal(f.subject.changingIds.value.has(plugin.id), true);
    f.calls[0].resolve(response({ ...plugin, state: 'Failed', canChangeEnabled: false, errors: ['校验失败'] }));
    await toggling;
    assert.equal(f.subject.changingIds.value.has(plugin.id), false);
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
    assert.equal(f.calls.length, 3);
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
      assert.equal(f.calls.length, 1);
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
  const models = evaluate('../../src/features/plugins/pluginModels.ts', () => ({}));
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
    f.calls[0].resolve(response({ id: 'first', enabled: false, manifest: { settings_ui: true } }));
    await setImmediate();
    f.calls[1].resolve(response({ registerOnHome: true }));
    await setImmediate();
    f.calls[2].resolve(response({}, false));
    f.calls[3].resolve(response([]));
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
    f.calls[0].resolve(response({ id: 'first', enabled: false, manifest: { settings_ui: true } }));
    await setImmediate();
    f.calls[1].resolve(response({ registerOnHome: true }));
    await setImmediate();
    f.calls[2].resolve({ ok: true, json: () => new Promise(resolve => { releaseBody = resolve; }) });
    f.calls[3].resolve(response([]));
    await setImmediate();
    const second = f.subject.load();
    f.calls[4].resolve(response({ id: 'first', enabled: false, manifest: { settings_ui: true } }));
    await setImmediate();
    f.calls[5].resolve(response({ registerOnHome: true }));
    await setImmediate();
    f.calls[6].resolve(response({ important: 'new' }));
    f.calls[7].resolve(response([]));
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

test('plugin info reload failures preserve previously loaded metadata', async () => {
  const f = fixture();
  try {
    const initial = f.subject.load();
    f.calls[0].resolve(response({ id: 'first', hasReadme: true }));
    await initial;
    const retry = f.subject.load();
    f.calls[1].reject(new Error('无法连接到 WinPilot'));
    await retry;
    assert.equal(f.subject.plugin.value.id, 'first');
    assert.equal(f.subject.readmeAvailable.value, true);
    assert.equal(f.toasts.length, 0);
  } finally { f.dispose(); }
});

test('plugin details provide readme availability without an extra request', async () => {
  const f = trackedRequestFixture();
  try {
    const loading = f.subject.load();
    assert.equal(f.calls[0].url, '/api/plugins/first');
    f.calls[0].resolve(response({ id: 'first', hasReadme: true }));
    await loading;
    assert.equal(f.subject.readmeAvailable.value, true);
    assert.equal(f.calls.length, 1);
    assert.equal(f.controllers.size, 0);
    const reloading = f.subject.load();
    assert.equal(f.subject.readmeAvailable.value, true);
    f.calls[1].resolve(response({ id: 'first', hasReadme: false }));
    await reloading;
    assert.equal(f.subject.readmeAvailable.value, false);
    assert.equal(f.calls.length, 2);
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
