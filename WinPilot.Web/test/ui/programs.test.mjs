import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';

async function fixture() {
  const code = loadScriptModule(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url),
    'launch, saveTag, launchTag, queryPrograms, collapsePrograms, showUsage, refreshUsage, closeUsage, processUsage, processUsageTarget, isUsageLoading, isQuerying, hasQueried, programs, groups, selectedUserName, tagName, pendingActions, launchFormError, pageError');
  let mounted;
  let unmount;
  const requests = [];
  const toasts = [];
  const context = {
    exports: {}, AbortController,
    require: name => {
      if (name === 'vue') return { ...vue, onMounted: callback => { mounted = callback; }, onBeforeUnmount: callback => { unmount = callback; } };
      if (name.endsWith('/client')) return {
        apiFetch: (url, init) => new Promise((resolve, reject) => requests.push({ url, init, resolve, reject })),
        isAbortError: error => error?.name === 'AbortError', userErrorMessage: error => error.message,
      };
      if (name.endsWith('/toast')) return { showToast: (text, type) => toasts.push({ text, type }) };
      if (name.endsWith('/safeMode')) return { safeModeEnabled: vue.ref(false) };
      return {};
    },
  };
  const scope = vue.effectScope();
  scope.run(() => runInNewContext(code, context));
  mounted();
  requests[0].resolve({ ok: true, json: async () => [] });
  requests[1].resolve({ ok: true, json: async () => [{ userName: 'User', isCurrentUser: true }] });
  await setImmediate();
  requests.length = 0;
  return { subject: context.exports, requests, toasts, dispose: () => { unmount(); scope.stop(); } };
}

test('program writes do not cancel each other and duplicate submissions are ignored', async () => {
  const f = await fixture();
  try {
    f.subject.tagName.value = 'Tag';
    const launch = f.subject.launch();
    const save = f.subject.saveTag();
    await f.subject.launch();
    await f.subject.saveTag();
    assert.equal(f.requests.length, 2);
    assert.ok(f.requests.every(request => !request.init.signal.aborted));
    f.requests[0].resolve({ ok: true });
    f.requests[1].resolve({ ok: true });
    await setImmediate();
    f.requests[2].resolve({ ok: true, json: async () => [] });
    await Promise.all([launch, save]);
    assert.equal(f.subject.pendingActions.size, 0);
    assert.equal(f.toasts.length, 2);
  } finally { f.dispose(); }
});

test('closing a usage dialog prevents a late result from repopulating it', async () => {
  const f = await fixture();
  try {
    f.subject.showUsage({ name: 'App', processIds: [1] });
    assert.equal(f.requests.length, 0);
    assert.equal(f.subject.processUsage.value, undefined);
    assert.equal(f.subject.isUsageLoading.value, false);
    const pending = f.subject.refreshUsage();
    f.subject.closeUsage();
    assert.equal(f.requests[0].init.signal.aborted, true);
    f.requests[0].resolve({ ok: true, json: async () => ({ cpuPercent: 1 }) });
    await pending;
    assert.equal(f.subject.processUsageTarget.value, undefined);
    assert.equal(f.subject.processUsage.value, undefined);
    assert.equal(f.subject.isUsageLoading.value, false);
  } finally { f.dispose(); }
});

test('usage results remain until the dialog closes and a new dialog starts empty', async () => {
  const f = await fixture();
  try {
    const target = { name: 'App', processIds: [1] };
    f.subject.showUsage(target);
    const pending = f.subject.refreshUsage();
    await f.subject.refreshUsage();
    assert.equal(f.requests.length, 1);
    f.requests[0].resolve({ ok: true, json: async () => ({ cpuPercent: 1, memoryBytes: 100, gpuPercent: null }) });
    await pending;
    assert.equal(f.subject.processUsage.value.cpuPercent, 1);
    assert.equal(f.subject.isUsageLoading.value, false);
    assert.equal(f.requests.length, 1);
    f.subject.closeUsage();
    assert.equal(f.subject.processUsage.value, undefined);
    f.subject.showUsage(target);
    assert.equal(f.subject.processUsage.value, undefined);
    assert.equal(f.requests.length, 1);
  } finally { f.dispose(); }
});

test('collapsing a query invalidates its result and releases the querying state', async () => {
  const f = await fixture();
  try {
    const pending = f.subject.queryPrograms();
    f.subject.collapsePrograms();
    f.requests[0].resolve({ ok: true, json: async () => [{ name: 'App', processId: 1 }] });
    await pending;
    assert.equal(f.subject.hasQueried.value, false);
    assert.equal(f.subject.isQuerying.value, false);
    assert.equal(f.subject.programs.value.length, 0);
  } finally { f.dispose(); }
});

test('leaving the page aborts all independent writes without late state changes or toasts', async () => {
  const f = await fixture();
  const launch = f.subject.launch();
  const tag = f.subject.launchTag({ name: 'Tag' });
  f.dispose();
  assert.ok(f.requests.every(request => request.init.signal.aborted));
  f.requests[0].resolve({ ok: true });
  f.requests[1].reject(new Error('late failure'));
  await Promise.all([launch, tag]);
  assert.equal(f.toasts.length, 0);
  assert.equal(f.subject.launchFormError.value, '');
  assert.equal(f.subject.pendingActions.size, 0);
});

test('program grouping preserves all processes and alphabetical ordering', async () => {
  const f = await fixture();
  try {
    f.subject.programs.value = [{ name: 'B', processId: 1 }, { name: 'A', processId: 2 }, { name: 'B', processId: 3 }];
    assert.equal(f.subject.groups.value.map(group => group.name).join(','), 'A,B');
    assert.equal(f.subject.groups.value[1].processes.map(process => process.processId).join(','), '1,3');
  } finally { f.dispose(); }
});
