import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';
import { setImmediate } from 'node:timers/promises';

function fixture() {
  const calls = [], events = [], checks = [], redirects = [];
  const code = loadScriptModule(new URL('../../src/features/security/safeMode.ts', import.meta.url));
  const context = {
    exports: {}, AbortController, AbortSignal,
    fetch: (url, options) => new Promise((resolve, reject) => checks.push({ url, options, resolve, reject })),
    require: name => name === 'vue' ? vue : name.endsWith('/client')
      ? {
        apiFetch: (url, options) => new Promise((resolve, reject) => calls.push({ url, options, resolve, reject })),
        redirectToLogin: () => redirects.push('/login'),
      }
      : {},
    EventSource: class {
      constructor() { events.push(this); }
      close() { this.closed = true; }
    },
  };
  runInNewContext(code, context);
  return { subject: context.exports, calls, events, checks, redirects };
}

test('SSE errors redirect only after the current check confirms unauthorized access', async () => {
  for (const outcome of [401, 200, 403, 'network-error']) {
    const f = fixture();
    try {
      f.subject.startSafeModeSync();
      f.events[0].onerror();
      assert.equal(f.checks.length, 1);
      if (outcome === 'network-error') f.checks[0].reject(new Error('offline'));
      else f.checks[0].resolve(new Response(JSON.stringify({ enabled: true }), { status: outcome }));
      await setImmediate();
      assert.deepEqual(f.redirects, outcome === 401 ? ['/login'] : []);
      if (outcome === 200) assert.equal(f.subject.safeModeEnabled.value, true);
    } finally { f.subject.stopSafeModeSync(); }
  }
});

test('stopped, superseded or recovered SSE checks cannot redirect on a late 401', async () => {
  for (const action of ['stop', 'new-check', 'recovered']) {
    const f = fixture();
    try {
      f.subject.startSafeModeSync();
      f.events[0].onerror();
      if (action === 'stop') f.subject.stopSafeModeSync();
      else if (action === 'new-check') f.events[0].onerror();
      else f.events[0].onmessage({ data: JSON.stringify({ enabled: false }) });
      f.checks[0].resolve(new Response(null, { status: 401 }));
      await setImmediate();
      assert.deepEqual(f.redirects, []);
      if (action === 'new-check') {
        f.checks[1].resolve(new Response(null, { status: 401 }));
        await setImmediate();
        assert.deepEqual(f.redirects, ['/login']);
      }
    } finally { f.subject.stopSafeModeSync(); }
  }
});

test('late initial safe-mode reads cannot overwrite a newer SSE state', async () => {
  const f = fixture();
  f.subject.startSafeModeSync();
  const loading = f.subject.loadSafeMode();
  assert.equal(f.calls.length, 1);
  f.events[0].onmessage({ data: JSON.stringify({ enabled: true }) });
  f.calls[0].resolve({ ok: true, json: async () => ({ enabled: false }) });
  await loading;
  assert.equal(f.subject.safeModeEnabled.value, true);
  f.subject.stopSafeModeSync();
  assert.equal(f.events[0].closed, true);
});

test('stopping safe-mode sync ignores late reads and old stream events', async () => {
  const f = fixture();
  f.subject.startSafeModeSync();
  const loading = f.subject.loadSafeMode();
  f.subject.stopSafeModeSync();
  f.events[0].onmessage({ data: JSON.stringify({ enabled: true }) });
  f.calls[0].resolve({ ok: true, json: async () => ({ enabled: true }) });
  await loading;
  assert.equal(f.subject.safeModeEnabled.value, false);
  f.subject.startSafeModeSync();
  const retry = f.subject.loadSafeMode();
  f.calls[1].resolve({ ok: true, json: async () => ({ enabled: true }) });
  await retry;
  assert.equal(f.subject.safeModeEnabled.value, true);
  f.subject.stopSafeModeSync();
});

test('invalid safe-mode data does not mark initialization complete', async () => {
  const f = fixture();
  const loading = f.subject.loadSafeMode();
  f.calls[0].resolve({ ok: true, json: async () => ({}) });
  await loading;
  const retry = f.subject.loadSafeMode();
  f.calls[1].resolve({ ok: true, json: async () => ({ enabled: false }) });
  await retry;
  await f.subject.loadSafeMode();
  assert.equal(f.calls.length, 2);
});

test('stopping pending initialization aborts it and old completion cannot clear the restarted request', async () => {
  for (const outcome of ['response', 'abort']) {
    const f = fixture();
    try {
      f.subject.startSafeModeSync();
      const initial = f.subject.loadSafeMode();
      f.subject.stopSafeModeSync();
      assert.equal(f.calls[0].options.signal.aborted, true);
      assert.equal(f.events[0].closed, true);
      f.subject.startSafeModeSync();
      const restarted = f.subject.loadSafeMode();
      assert.equal(f.calls.length, 2);
      assert.equal(f.calls[1].options.signal.aborted, false);
      if (outcome === 'abort') f.calls[0].reject(new DOMException('cancelled', 'AbortError'));
      else f.calls[0].resolve({ ok: true, json: async () => ({ enabled: true }) });
      await initial;
      assert.equal(f.subject.safeModeEnabled.value, false);
      const shared = f.subject.loadSafeMode();
      assert.equal(f.calls.length, 2);
      f.calls[1].resolve({ ok: true, json: async () => ({ enabled: true }) });
      await Promise.all([restarted, shared]);
      assert.equal(f.subject.safeModeEnabled.value, true);
    } finally { f.subject.stopSafeModeSync(); }
  }
});

test('stopping during response parsing prevents late initialization from overwriting a new read', async () => {
  const f = fixture();
  try {
    const initial = f.subject.loadSafeMode();
    const body = Promise.withResolvers();
    f.calls[0].resolve({ ok: true, json: () => body.promise });
    await setImmediate();
    f.subject.stopSafeModeSync();
    assert.equal(f.calls[0].options.signal.aborted, true);
    const restarted = f.subject.loadSafeMode();
    f.calls[1].resolve({ ok: true, json: async () => ({ enabled: false }) });
    await restarted;
    body.resolve({ enabled: true });
    await initial;
    assert.equal(f.subject.safeModeEnabled.value, false);
    await f.subject.loadSafeMode();
    assert.equal(f.calls.length, 2);
  } finally { f.subject.stopSafeModeSync(); }
});
