import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';

function fixture() {
  const calls = [], toasts = [], events = [], intervals = new Map(), timeouts = new Map();
  let now = 100000, timer = 0, mount, unmount;
  class Clock extends Date {
    constructor(...args) { super(...(args.length ? args : [now])); }
    static now() { return now; }
  }
  const document = { visibilityState: 'visible', addEventListener() {}, removeEventListener() {} };
  const code = loadScriptModule(new URL('../../src/features/home/HomeView.vue', import.meta.url),
    'refreshStatus,refreshPlugins,pluginSummaries,pluginFeatureSummaries,relativeCapturedAt,executeFeature,confirmFeature,confirmingFeature,autoEnabled,configureAutomaticRefresh,handlePageResume');
  const context = { exports: {}, Date: Clock, AbortController, DOMException, document,
    EventSource: class { constructor() { events.push(this); } close() { this.closed = true; } },
    sessionStorage: { getItem: () => null },
    window: { setInterval: cb => { intervals.set(++timer, cb); return timer; }, clearInterval: id => intervals.delete(id),
      setTimeout: cb => { timeouts.set(++timer, cb); return timer; }, clearTimeout: id => timeouts.delete(id),
      addEventListener() {}, removeEventListener() {} },
    require: name => {
      if (name === 'vue') return { ...vue, onMounted: cb => { mount = cb; }, onBeforeUnmount: cb => { unmount = cb; } };
      if (name === 'vue-router') return { useRouter: () => ({}) };
      if (name.endsWith('/client')) return { apiFetch: (url, init) => new Promise(resolve => calls.push({ url, init, resolve })), isAbortError: e => e?.name === 'AbortError', userErrorMessage: e => e.message };
      if (name.endsWith('/toast')) return { showToast: (...args) => toasts.push(args) };
      return {};
    },
  };
  const scope = vue.effectScope();
  scope.run(() => runInNewContext(code, context));
  const subject = context.exports;
  mount();
  calls[0].resolve({ ok: true, json: async () => [] });
  return { subject, calls, toasts, events, intervals, timeouts, document,
    advance(ms) { now += ms; for (const cb of [...intervals.values()]) cb(); },
    dispose() { unmount(); scope.stop(); } };
}

test('home publishes plugin status and features together and retains both after a failed refresh', async () => {
  const f = fixture();
  try {
    await setImmediate();
    f.subject.pluginSummaries.value = [{ id: 'old' }];
    f.subject.pluginFeatureSummaries.value = [{ id: 'old' }];
    for (const failed of [false, true]) {
      const pending = f.subject.refreshPlugins();
      f.calls.at(-1).resolve({ ok: true, json: async () => [{
        id: failed ? 'failed' : 'new', enabled: true, state: 'Running', manifest: { status_ui: true, features_ui: true },
      }] });
      await setImmediate();
      f.calls.findLast(call => /^\/api\/plugins\/[^/]+\/status$/.test(call.url)).resolve({ ok: true, json: async () => ({ value: 1 }) });
      f.calls.findLast(call => call.url.endsWith('/settings/display/status')).resolve({ ok: true, json: async () => ({ registerOnHome: true, statusKeys: ['value'] }) });
      await setImmediate();
      const expected = failed ? 'new' : 'old';
      assert.equal(f.subject.pluginSummaries.value[0].id, expected);
      assert.equal(f.subject.pluginFeatureSummaries.value[0].id, expected);
      f.calls.findLast(call => /^\/api\/plugins\/[^/]+\/features$/.test(call.url)).resolve({ ok: true, json: async () => {
        if (failed) throw new Error('Invalid response');
        return { features: [{ id: 'run' }] };
      } });
      f.calls.findLast(call => call.url.endsWith('/settings/display/features')).resolve({ ok: true, json: async () => ({ registerOnHome: true, featureIds: ['run'] }) });
      await pending;
      assert.equal(f.subject.pluginSummaries.value[0].id, 'new');
      assert.equal(f.subject.pluginSummaries.value[0].status.value, 1);
      assert.equal(f.subject.pluginFeatureSummaries.value[0].id, 'new');
      assert.equal(f.subject.pluginFeatureSummaries.value[0].features[0].id, 'run');
      assert.equal(f.events.length, 1);
    }
    assert.equal(f.events[0].closed, true);
  } finally { f.dispose(); }
});

test('home clock starts on capture and stops at one hour or while hidden', async () => {
  const f = fixture();
  try {
    assert.equal(f.intervals.size, 0);
    const refresh = f.subject.refreshStatus();
    f.calls.at(-1).resolve({ ok: true, json: async () => ({ cpuUsagePercent: 1, memoryUsagePercent: 1 }) });
    await refresh;
    assert.equal(f.intervals.size, 1);
    f.advance(7200000);
    assert.equal(f.subject.relativeCapturedAt.value, '1 小时前');
    assert.equal(f.intervals.size, 0);
    f.document.visibilityState = 'hidden';
    f.subject.autoEnabled.value = true;
    f.subject.configureAutomaticRefresh();
    assert.equal(f.timeouts.size, 0);
  } finally { f.dispose(); }
});

test('home confirmed actions share execution and ignore replies after leaving', async () => {
  const f = fixture();
  f.subject.confirmingFeature.value = { plugin: { id: 'sample' }, feature: { id: 'run', mode: 'action' } };
  const execution = f.subject.confirmFeature();
  await f.subject.confirmFeature();
  const call = f.calls.at(-1);
  assert.equal(f.calls.filter(c => c.url.endsWith('/execute')).length, 1);
  f.dispose();
  assert.equal(call.init.signal.aborted, true);
  call.resolve({ ok: true, json: async () => ({ succeeded: true, message: 'done' }) });
  await execution;
  assert.equal(f.toasts.length, 0);
});
