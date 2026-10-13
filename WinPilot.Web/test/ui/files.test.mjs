import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';
import * as apiErrors from '../support/apiErrors.mjs';

function fixture() {
  const code = loadScriptModule(new URL('../../src/features/files/FilesView.vue', import.meta.url),
    'browse, browseTarget, resetTargetDirectory, startEdit, confirmEditSave, submitFileOperation, uploadSelectedFiles, confirmDownload, directory, targetDirectory, targetPath, editTarget, editContent, editOriginalLength, uploadFiles, uploadDirectoryPath, uploadItems, downloadTargets, transferKind, requests, loading, submitting');
  const requests = [], toasts = [], revokedUrls = [];
  const anchor = { click() {}, remove() {} };
  const safeModeEnabled = vue.ref(false);
  let unmount;
  const context = {
    exports: {}, AbortController, DOMException, FormData,
    URL: { createObjectURL: () => 'blob:download', revokeObjectURL: url => revokedUrls.push(url) },
    document: { createElement: () => anchor, body: { append() {} } },
    require: name => {
      if (name === 'vue') return { ...vue, onMounted: () => {}, onBeforeUnmount: callback => { unmount = callback; } };
      if (name.endsWith('/client')) return { ...apiErrors,
        apiFetch: (url, init) => new Promise((resolve, reject) => requests.push({ url, init, resolve, reject })),
        isAbortError: error => error?.name === 'AbortError',
        userErrorMessage: error => error.message,
      };
      if (name.endsWith('/toast')) return { showToast: (text, type) => toasts.push({ text, type }) };
      if (name.endsWith('/safeMode')) return { safeModeEnabled };
      return {};
    },
  };
  const scope = vue.effectScope();
  scope.run(() => runInNewContext(code, context));
  return { subject: context.exports, requests, toasts, safeModeEnabled, anchor, revokedUrls, dispose: () => { unmount(); scope.stop(); } };
}
const directory = path => ({ path, entries: [], parentPath: null, isSensitiveSystemPath: false });
const ok = value => ({ ok: true, json: async () => value });

test('directory response bodies cannot overwrite a newer navigation', async () => {
  const f = fixture();
  try {
    let resolveBody;
    const first = f.subject.browse('first');
    f.requests[0].resolve({ ok: true, json: () => new Promise(resolve => { resolveBody = resolve; }) });
    await setImmediate();
    const second = f.subject.browse('second');
    assert.equal(f.requests[0].init.signal.aborted, true);
    f.requests[1].resolve(ok(directory('second')));
    await second;
    resolveBody(directory('first'));
    await first;
    assert.equal(f.subject.directory.value.path, 'second');
    assert.equal(f.subject.loading.value, false);
    assert.equal(f.toasts.length, 0);
  } finally { f.dispose(); }
});

test('target selection reset and dialog close invalidate pending results', async () => {
  const f = fixture();
  try {
    f.subject.transferKind.value = 'copy';
    const first = f.subject.browseTarget('first');
    const second = f.subject.browseTarget('second');
    f.requests[1].resolve(ok(directory('second')));
    await second;
    f.requests[0].resolve(ok(directory('first')));
    await first;
    assert.equal(f.subject.targetPath.value, 'second');
    const reset = f.subject.browseTarget('reset');
    f.subject.resetTargetDirectory();
    f.requests[2].resolve(ok(directory('reset')));
    await reset;
    assert.equal(f.subject.targetDirectory.value, undefined);
    const closed = f.subject.browseTarget('closed');
    f.subject.transferKind.value = undefined;
    f.requests[3].resolve(ok(directory('closed')));
    await closed;
    assert.equal(f.subject.targetPath.value, '');
  } finally { f.dispose(); }
});

test('writes reject duplicate submission and failed writes retain form state', async () => {
  const f = fixture();
  try {
    const first = f.subject.submitFileOperation('/api/files/file', {}, 'created');
    assert.equal(await f.subject.submitFileOperation('/api/files/file', {}, 'created'), false);
    assert.equal(f.requests.length, 1);
    assert.equal(f.subject.submitting.value, true);
    f.requests[0].resolve({ ok: false, json: async () => ({ detail: 'existing' }) });
    assert.equal(await first, false);
    assert.equal(f.toasts.at(-1).text, 'existing');
    assert.equal(f.subject.submitting.value, false);
  } finally { f.dispose(); }
});

test('edit reads are latest-only and saving uses the original target snapshot', async () => {
  const f = fixture();
  try {
    const first = f.subject.startEdit({ path: 'first' });
    const second = f.subject.startEdit({ path: 'second' });
    f.requests[1].resolve(ok({ content: 'new', length: 3, lastWriteTime: 'time', encoding: 'utf-8' }));
    await second;
    f.requests[0].resolve(ok({ content: 'old' }));
    await first;
    assert.equal(f.subject.editTarget.value.path, 'second');
    assert.equal(f.subject.editContent.value, 'new');
    const save = f.subject.confirmEditSave();
    await f.subject.confirmEditSave();
    assert.equal(f.requests.length, 3);
    f.subject.editTarget.value = undefined;
    f.requests[2].resolve({ ok: true });
    await setImmediate();
    assert.ok(f.requests[3].url.endsWith('path=second'));
    f.requests[3].resolve(ok({ length: 9, lastWriteTime: 'later' }));
    await save;
    assert.equal(f.subject.editTarget.value, undefined);
    assert.equal(f.subject.editOriginalLength.value, 3);
  } finally { f.dispose(); }
});

test('leaving aborts reads writes uploads and downloads without late effects', async () => {
  const f = fixture();
  f.subject.directory.value = directory('original');
  f.subject.uploadFiles.value = [new File(['x'], 'file.txt')];
  f.subject.uploadDirectoryPath.value = 'original';
  f.subject.downloadTargets.value = [{ path: 'file.txt', name: 'file.txt' }];
  const pending = [
    f.subject.browse('late'),
    f.subject.submitFileOperation('/api/files/file', {}, 'created'),
    f.subject.uploadSelectedFiles(),
    f.subject.confirmDownload(),
  ];
  f.dispose();
  assert.ok(f.requests.every(request => request.init.signal.aborted));
  f.requests.forEach(request => request.resolve(ok([])));
  await Promise.all(pending);
  assert.equal(f.subject.directory.value.path, 'original');
  assert.equal(f.subject.requests.size, 0);
  assert.equal(f.subject.uploadFiles.value.length, 0);
  assert.equal(f.toasts.length, 0);
});

test('safe mode cancels pending requests and clears open editors immediately', async () => {
  const f = fixture();
  try {
    f.subject.editTarget.value = { path: 'file' };
    f.subject.editContent.value = 'secret';
    const pending = f.subject.browse('late');
    f.safeModeEnabled.value = true;
    assert.equal(f.requests[0].init.signal.aborted, true);
    assert.equal(f.subject.editTarget.value, undefined);
    assert.equal(f.subject.editContent.value, '');
    f.requests[0].resolve(ok(directory('late')));
    await pending;
    assert.equal(f.subject.directory.value, undefined);
    assert.equal(f.toasts.length, 0);
  } finally { f.dispose(); }
});

test('uploads use their selected directory and do not navigate back after completion', async () => {
  const f = fixture();
  try {
    f.subject.directory.value = directory('new-location');
    f.subject.uploadDirectoryPath.value = 'selected-location';
    f.subject.uploadFiles.value = [new File(['content'], 'file.txt')];
    const pending = f.subject.uploadSelectedFiles();
    assert.equal(f.requests[0].init.body.get('directory'), 'selected-location');
    assert.equal(f.subject.uploadFiles.value.length, 0);
    f.requests[0].resolve(ok([{ fileName: 'file.txt', success: true }]));
    await pending;
    assert.equal(f.requests.length, 1);
    assert.equal(f.subject.directory.value.path, 'new-location');
    assert.equal(f.subject.uploadItems.value[0].status, 'success');
  } finally { f.dispose(); }
});

test('download releases its blob URL even if the browser click fails', async () => {
  const f = fixture();
  try {
    f.subject.downloadTargets.value = [{ path: 'file', name: 'file' }];
    f.anchor.click = () => { throw new Error('download failed'); };
    const pending = f.subject.confirmDownload();
    f.requests[0].resolve({ ok: true, blob: async () => new Blob(['content']) });
    await pending;
    assert.deepEqual(f.revokedUrls, ['blob:download']);
    assert.equal(f.subject.requests.size, 0);
    assert.equal(f.toasts.at(-1).text, 'download failed');
  } finally { f.dispose(); }
});
