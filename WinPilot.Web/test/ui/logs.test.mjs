import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as delayedLoading from '../../src/ui/useDelayedLoading.ts';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import { elements, binding } from '../support/template.mjs';
import * as vue from 'vue';
import * as apiErrors from '../support/apiErrors.mjs';

function fixture() {
  const code = loadScriptModule(new URL('../../src/features/logs/LogsView.vue', import.meta.url),
    'refreshLogs, loadEntries, retryLoad, toggleLevel, dates, selectedDate, selectedLevels, keyword, entries, hasMore, isLoading, isRefreshing, isLoadingMore, pageError, retryLoadingMore');
  const requests = [];
  let mount, unmount;
  const context = {
    exports: {}, AbortController, DOMException, URLSearchParams,
    require: name => {
      if (name.endsWith('/useDelayedLoading')) return delayedLoading;
      if (name === 'vue') return { ...vue, useId: () => 'logs-test', onMounted: callback => { mount = callback; }, onBeforeUnmount: callback => { unmount = callback; } };
      if (name.endsWith('/client')) return { ...apiErrors,
        apiFetch: (url, init) => new Promise((resolve, reject) => requests.push({ url, init, resolve, reject })),
        isAbortError: error => error?.name === 'AbortError', userErrorMessage: error => error.message,
      };
      return {};
    },
  };
  const scope = vue.effectScope();
  scope.run(() => runInNewContext(code, context));
  mount();
  return { subject: context.exports, requests, dispose: () => { unmount(); scope.stop(); } };
}
const ok = value => ({ ok: true, json: async () => value });
const page = (id, hasMore = true, snapshot = 3) => ({ entries: [{ id }], hasMore, snapshot });

test('initial loading omits refresh feedback and manual refresh covers dates and entries', async () => {
  const f = fixture();
  try {
    assert.equal(f.subject.isLoading.value, true);
    assert.equal(f.subject.isRefreshing.value, false);
    f.requests[0].resolve(ok([]));
    await setImmediate();
    assert.equal(f.subject.isLoading.value, false);
    const refresh = f.subject.refreshLogs();
    assert.equal(f.subject.isRefreshing.value, true);
    f.requests[1].resolve(ok([{ date: '2026-10-04' }]));
    await setImmediate();
    assert.equal(f.subject.isLoading.value, true);
    assert.equal(f.subject.isRefreshing.value, true);
    f.requests[2].resolve(ok(page(3)));
    await refresh;
    assert.equal(f.subject.entries.value[0].id, 3);
    assert.equal(f.subject.isLoading.value, false);
    assert.equal(f.subject.isRefreshing.value, false);
  } finally { f.dispose(); }
});

test('log refresh preserves the previous error until the retry completes', async () => {
  const source = readFileSync(new URL('../../src/features/logs/LogsView.vue', import.meta.url), 'utf8');
  const errors = elements(source, node => node.tag === 'AppLoadError');
  assert.equal(errors.length, 1);
  assert.equal(binding(errors[0], 'retry'), 'retryLoad');
  const f = fixture();
  try {
    f.requests[0].reject(new Error('offline'));
    await setImmediate();
    assert.equal(f.subject.pageError.value, 'offline');
    const retry = f.subject.retryLoad();
    assert.equal(f.subject.pageError.value, 'offline');
    f.requests[1].resolve(ok([]));
    await retry;
    assert.equal(f.subject.pageError.value, '');
  } finally { f.dispose(); }
});

test('late failures and response bodies cannot replace a newer result', async () => {
  const f = fixture();
  try {
    const fresh = f.subject.refreshLogs();
    f.requests[0].reject(new Error('stale error'));
    f.requests[1].resolve(ok([{ date: '2026-10-04' }]));
    await setImmediate();
    assert.equal(f.subject.isRefreshing.value, true);
    let resolveBody;
    f.requests[2].resolve({ ok: true, json: () => new Promise(resolve => { resolveBody = resolve; }) });
    await setImmediate();
    const newer = f.subject.loadEntries(true);
    assert.equal(f.subject.isRefreshing.value, false);
    f.requests[3].resolve(ok(page(4)));
    await newer;
    resolveBody(page(3));
    await fresh;
    assert.equal(f.subject.entries.value[0].id, 4);
    assert.equal(f.subject.pageError.value, '');
  } finally { f.dispose(); }
});

test('load more reuses the snapshot and deduplicates clicks, while filter changes reset it', async () => {
  const f = fixture();
  try {
    f.requests[0].resolve(ok([{ date: '2026-10-04' }]));
    await setImmediate();
    f.requests[1].resolve(ok(page(3)));
    await setImmediate();
    const more = f.subject.loadEntries(false);
    assert.equal(f.subject.isLoading.value, true);
    assert.equal(f.subject.isLoadingMore.value, true);
    assert.equal(f.subject.isRefreshing.value, false);
    await f.subject.loadEntries(false);
    assert.equal(f.requests.length, 3);
    assert.match(f.requests[2].url, /snapshot=3/);
    assert.equal(new URLSearchParams(f.requests[2].url.split('?')[1]).get('limit'), '100');
    assert.match(f.requests[2].url, /offset=1/);
    f.requests[2].resolve(ok(page(2, false)));
    await more;
    assert.equal(f.subject.isLoadingMore.value, false);
    assert.deepEqual(Array.from(f.subject.entries.value, entry => entry.id), [3, 2]);
    await f.subject.loadEntries(false);
    assert.equal(f.requests.length, 3);
    f.subject.keyword.value = 'error';
    const filtered = f.subject.loadEntries(true);
    assert.equal(f.subject.isLoadingMore.value, false);
    assert.equal(f.subject.isRefreshing.value, false);
    assert.ok(!f.requests[3].url.includes('snapshot='));
    assert.match(f.requests[3].url, /offset=0/);
    assert.equal(new URLSearchParams(f.requests[3].url.split('?')[1]).get('limit'), '100');
    f.requests[3].resolve(ok(page(8, false, 8)));
    await filtered;
    assert.equal(f.subject.entries.value[0].id, 8);
  } finally { f.dispose(); }
});

test('empty levels clear pagination without a request and failed loads retain existing rows', async () => {
  const f = fixture();
  try {
    f.requests[0].resolve(ok([{ date: '2026-10-04' }]));
    await setImmediate();
    f.requests[1].resolve(ok(page(3)));
    await setImmediate();
    const failed = f.subject.loadEntries(false);
    f.requests[2].resolve({ ok: false, json: async () => ({ detail: '日志文件暂时无法读取' }) });
    await failed;
    assert.equal(f.subject.pageError.value, '日志文件暂时无法读取');
    assert.equal(f.subject.entries.value[0].id, 3);
    f.subject.selectedLevels.value = [];
    await f.subject.loadEntries(true);
    assert.equal(f.subject.entries.value.length, 0);
    assert.equal(f.subject.hasMore.value, false);
    assert.equal(f.requests.length, 3);
  } finally { f.dispose(); }
});

test('failed append retries the same offset and snapshot without discarding previous rows', async () => {
  const f = fixture();
  try {
    f.requests[0].resolve(ok([{ date: '2026-10-04' }]));
    await setImmediate();
    f.requests[1].resolve(ok(page(3)));
    await setImmediate();
    const failed = f.subject.loadEntries(false);
    f.requests[2].reject(new Error('offline'));
    await failed;
    assert.equal(f.subject.retryLoadingMore.value, true);
    const retry = f.subject.retryLoad();
    assert.equal(f.requests[3].url, f.requests[2].url);
    assert.equal(f.subject.entries.value.length, 1);
    assert.equal(f.subject.pageError.value, 'offline');
    f.requests[3].resolve(ok(page(2, false)));
    await retry;
    assert.deepEqual(Array.from(f.subject.entries.value, entry => entry.id), [3, 2]);
    assert.equal(f.subject.retryLoadingMore.value, false);
    assert.equal(f.subject.pageError.value, '');
  } finally { f.dispose(); }
});

test('unmount cancels all loading and suppresses late results and errors', async () => {
  const f = fixture();
  f.dispose();
  assert.equal(f.subject.isLoading.value, false);
  assert.equal(f.subject.isRefreshing.value, false);
  assert.equal(f.requests[0].init.signal.aborted, true);
  f.requests[0].reject(new Error('late'));
  await setImmediate();
  assert.equal(f.subject.pageError.value, '');
  assert.equal(f.subject.entries.value.length, 0);
  await f.subject.refreshLogs();
  assert.equal(f.requests.length, 1);
});
