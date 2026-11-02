import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';

test('network failures stay local to each request', async () => {
  const { api, calls } = authenticationFixture();
  const older = api.apiFetch('/old');
  const newer = api.apiFetch('/new');
  calls[1].resolve({ ok: true });
  await newer;
  calls[0].reject(new Error('late failure'));
  await assert.rejects(older, api.WinPilotConnectionError);
  assert.equal(api.userErrorMessage(new api.WinPilotConnectionError(), '失败'), '无法连接到 WinPilot');
  const failure = api.apiFetch('/failure');
  calls[2].reject(new Error('current failure'));
  await assert.rejects(failure);
});

function authenticationFixture(path = '/files') {
  const calls = [], navigations = [];
  const location = new URL(path, 'http://127.0.0.1:5186');
  location.assign = url => navigations.push(url);
  const context = {
    exports: {}, DOMException, URL,
    window: { location },
    fetch: (input, init) => new Promise((resolve, reject) => calls.push({ input, init, resolve, reject })),
  };
  runInNewContext(loadScriptModule(new URL('../../src/api/client.ts', import.meta.url)), context);
  return { api: context.exports, calls, navigations, location };
}

test('expired API requests redirect without waiting for an SSE disconnect and preserve the response', async () => {
  const inputs = [
    '/api/files/roots',
    new URL('http://127.0.0.1:5186/api/plugins'),
    new Request('http://127.0.0.1:5186/api/tasks'),
    '/plugin-assets/demo/index.js',
  ];
  for (const input of inputs) {
    const f = authenticationFixture();
    const pending = f.api.apiFetch(input);
    const response = new Response(null, { status: 401 });
    f.calls[0].resolve(response);
    assert.equal(await pending, response);
    assert.deepEqual(f.navigations, ['/login']);
  }
});

test('concurrent unauthorized responses and SSE expiry share a single login navigation', async () => {
  for (const sseFirst of [false, true]) {
    const f = authenticationFixture();
    const requests = ['/api/files/roots', '/api/plugins'].map(url => f.api.apiFetch(url));
    if (sseFirst) f.api.redirectToLogin();
    for (const call of f.calls) call.resolve(new Response(null, { status: 401 }));
    await Promise.all(requests);
    f.api.redirectToLogin();
    assert.deepEqual(f.navigations, ['/login']);
  }
});

test('login errors, external endpoints and unrelated paths never trigger session navigation', async () => {
  for (const input of ['/login', 'http://example.test/api/data', '/apiary', '/plugin-assets-other/demo']) {
    const f = authenticationFixture();
    const pending = f.api.apiFetch(input);
    f.calls[0].resolve(new Response(null, { status: 401 }));
    assert.equal((await pending).status, 401);
    assert.deepEqual(f.navigations, []);
  }
  const f = authenticationFixture('/login');
  const pending = f.api.apiFetch('/api/tasks');
  f.calls[0].resolve(new Response(null, { status: 401 }));
  await pending;
  f.api.redirectToLogin();
  assert.deepEqual(f.navigations, []);
});

test('permission errors, server errors, network failures and cancellation never redirect to login', async () => {
  const f = authenticationFixture();
  for (const status of [200, 403, 500]) {
    const pending = f.api.apiFetch('/api/tasks');
    f.calls.at(-1).resolve(new Response(null, { status }));
    assert.equal((await pending).status, status);
  }
  const offline = f.api.apiFetch('/api/tasks');
  f.calls.at(-1).reject(new TypeError('Failed to fetch'));
  await assert.rejects(offline, f.api.WinPilotConnectionError);
  const aborted = f.api.apiFetch('/api/tasks');
  const error = new DOMException('cancelled', 'AbortError');
  f.calls.at(-1).reject(error);
  await assert.rejects(aborted, error);
  assert.deepEqual(f.navigations, []);
});

test('navigation errors are not misreported as network failures', async () => {
  const f = authenticationFixture();
  const error = new Error('navigation failed');
  f.location.assign = () => { throw error; };
  const pending = f.api.apiFetch('/api/files/roots');
  f.calls[0].resolve(new Response(null, { status: 401 }));
  await assert.rejects(pending, error);
});
