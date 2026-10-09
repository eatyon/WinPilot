import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';

function fixture() {
  const calls = [], events = [];
  const code = loadScriptModule(new URL('../../src/features/security/safeMode.ts', import.meta.url));
  const context = {
    exports: {},
    require: name => name === 'vue' ? vue : name.endsWith('/client')
      ? { apiFetch: () => new Promise(resolve => calls.push({ resolve })) }
      : { markConnectionRestored() {}, connectionRestoreVersion: () => 0, markConnectionLostUnlessRestored() {} },
    EventSource: class {
      constructor() { events.push(this); }
      close() { this.closed = true; }
    },
  };
  runInNewContext(code, context);
  return { subject: context.exports, calls, events };
}

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
