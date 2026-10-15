import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';

test('an older failed API request cannot overwrite a newer successful connection', async () => {
  const calls = [];
  let version = 0, state = 'online';
  const context = {
    exports: {}, DOMException,
    fetch: () => new Promise((resolve, reject) => calls.push({ resolve, reject })),
    require: () => ({ connectionRestoreVersion: () => version,
      markConnectionRestored() { version++; state = 'online'; },
      markConnectionLostUnlessRestored(since) { if (since === version) state = 'disconnected'; } }),
  };
  runInNewContext(loadScriptModule(new URL('../../src/api/client.ts', import.meta.url)), context);
  const api = context.exports;
  const older = api.apiFetch('/old');
  const newer = api.apiFetch('/new');
  calls[1].resolve({ ok: true });
  await newer;
  calls[0].reject(new Error('late failure'));
  await assert.rejects(older, api.WinPilotConnectionError);
  assert.equal(state, 'online');
  const failure = api.apiFetch('/failure');
  calls[2].reject(new Error('current failure'));
  await assert.rejects(failure);
  assert.equal(state, 'disconnected');
});
