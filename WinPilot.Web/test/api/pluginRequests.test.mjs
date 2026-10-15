import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import { combineRequestSignal } from '../../src/api/requestSignal.ts';

function evaluate(path, require, globals = {}) {
  const context = { exports: {}, require, AbortController, DOMException, ...globals };
  runInNewContext(loadScriptModule(new URL(path, import.meta.url)), context);
  return context.exports;
}

function trackedRequestFixture() {
  const calls = [], maps = [];
  const client = {
    apiFetch: (url, init) => new Promise((resolve, reject) => calls.push({ url, init, resolve, reject })),
    isAbortError: error => error?.name === 'AbortError',
  };
  const executor = evaluate('../../src/api/requestExecutor.ts', name =>
    name.endsWith('/client') ? client : { combineRequestSignal });
  const module = evaluate('../../src/features/plugins/PluginRequests.ts', () => executor, {
    Map: class extends Map {
      constructor(...args) { super(...args); maps.push(this); }
    },
  });
  const requests = module.createPluginRequests();
  assert.equal(maps.length, 1);
  return { calls, subject: { requests }, controllers: maps[0], dispose: () => requests.dispose() };
}

const response = (value, ok = true) => ({ ok, json: async () => value });

function observeAbortListeners(signal) {
  const listeners = new Set();
  const add = signal.addEventListener.bind(signal);
  const remove = signal.removeEventListener.bind(signal);
  signal.addEventListener = (type, listener, options) => {
    if (type === 'abort') listeners.add(listener);
    add(type, listener, options);
  };
  signal.removeEventListener = (type, listener, options) => {
    if (type === 'abort') listeners.delete(listener);
    remove(type, listener, options);
  };
  return { get count() { return listeners.size; } };
}

test('cancelled late network errors are treated as cancellation', async () => {
  const f = trackedRequestFixture();
  try {
    const pending = f.subject.requests.run('/test', {}, response => response.json());
    f.subject.requests.cancelAll();
    f.calls[0].reject(new Error('late network error'));
    await assert.rejects(pending, error => error.name === 'AbortError');
  } finally { f.dispose(); }
});

test('completed plugin JSON immediately releases tracking and merged abort listeners', async () => {
  const f = trackedRequestFixture();
  const external = new AbortController();
  const listeners = observeAbortListeners(external.signal);
  try {
    const opening = f.subject.requests.run('/lifecycle-json', { signal: external.signal }, response => response.json());
    assert.deepEqual({ controllers: f.controllers.size, listeners: listeners.count }, { controllers: 1, listeners: 1 });
    f.calls[0].resolve(response({ value: 'complete' }));
    const result = await opening;
    assert.equal(result.value, 'complete');
    assert.deepEqual({ controllers: f.controllers.size, listeners: listeners.count }, { controllers: 0, listeners: 0 });
  } finally { f.dispose(); }
});

test('completed plugin text immediately releases tracking and merged abort listeners', async () => {
  const f = trackedRequestFixture();
  const external = new AbortController();
  const listeners = observeAbortListeners(external.signal);
  try {
    const opening = f.subject.requests.run('/lifecycle-text', { signal: external.signal }, response => response.text());
    assert.deepEqual({ controllers: f.controllers.size, listeners: listeners.count }, { controllers: 1, listeners: 1 });
    f.calls[0].resolve({ ok: true, text: async () => '# Document' });
    assert.equal(await opening, '# Document');
    assert.deepEqual({ controllers: f.controllers.size, listeners: listeners.count }, { controllers: 0, listeners: 0 });
  } finally { f.dispose(); }
});

test('late cleanup of an old plugin body preserves the newer same-key request', async () => {
  const f = trackedRequestFixture();
  const body = Promise.withResolvers();
  let oldOutcome, newOutcome;
  try {
    oldOutcome = f.subject.requests.run('/lifecycle-shared', {}, response => response.json()).then(value => ({ value }), error => ({ error }));
    f.calls[0].resolve({ ok: true, json: () => body.promise });
    await setImmediate();
    newOutcome = f.subject.requests.run('/lifecycle-shared', {}, response => response.json()).then(value => ({ value }), error => ({ error }));
    const newest = f.controllers.get('GET:/lifecycle-shared');
    assert.ok(newest);
    assert.equal(f.calls[0].init.signal.aborted, true);
    body.resolve({ value: 'old' });
    assert.equal((await oldOutcome).error?.name, 'AbortError');
    assert.equal(f.controllers.size, 1);
    assert.equal(f.controllers.get('GET:/lifecycle-shared'), newest);
    assert.equal(f.calls[1].init.signal.aborted, false);
    f.subject.requests.cancelReads();
    assert.equal(f.calls[1].init.signal.aborted, true);
    f.calls[1].resolve(response({}));
    assert.equal((await newOutcome).error?.name, 'AbortError');
  } finally {
    f.dispose();
    body.resolve({});
    for (const call of f.calls) call.resolve(response({}));
    await Promise.all([oldOutcome, newOutcome].filter(Boolean));
  }
});

test('late cleanup of an old plugin fetch failure preserves the newer same-key request', async () => {
  const f = trackedRequestFixture();
  let oldOutcome, newOutcome;
  try {
    oldOutcome = f.subject.requests.run('/lifecycle-shared-failure', {}, response => response.json()).then(value => ({ value }), error => ({ error }));
    newOutcome = f.subject.requests.run('/lifecycle-shared-failure', {}, response => response.json()).then(value => ({ value }), error => ({ error }));
    const newest = f.controllers.get('GET:/lifecycle-shared-failure');
    assert.ok(newest);
    f.calls[0].reject(new Error('late network failure'));
    assert.equal((await oldOutcome).error?.name, 'AbortError');
    assert.equal(f.controllers.size, 1);
    assert.equal(f.controllers.get('GET:/lifecycle-shared-failure'), newest);
    assert.equal(f.calls[1].init.signal.aborted, false);
    f.subject.requests.cancelAll();
    assert.equal(f.calls[1].init.signal.aborted, true);
    f.calls[1].resolve(response({}));
    assert.equal((await newOutcome).error?.name, 'AbortError');
  } finally {
    f.dispose();
    for (const call of f.calls) call.resolve(response({}));
    await Promise.all([oldOutcome, newOutcome].filter(Boolean));
  }
});
