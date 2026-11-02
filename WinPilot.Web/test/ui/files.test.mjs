import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as delayedLoading from '../../src/ui/useDelayedLoading.ts';
import { modalStateModule } from '../support/modalState.mjs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import { elements, attribute, binding } from '../support/template.mjs';
import * as vue from 'vue';
import * as fileSelection from '../../src/features/files/useFileSelection.ts';
import * as apiErrors from '../support/apiErrors.mjs';

test('file creation fields use vertical library spacing with separate actions', () => {
  const source = readFileSync(new URL('../../src/features/files/FilesView.vue', import.meta.url), 'utf8');
  const dialogs = elements(source, node => node.tag === 'AppModal' && attribute(node, 'title') === '新建');
  assert.equal(dialogs.length, 1);
  const layout = dialogs[0].children.find(node => node.type === 1 && node.tag === 'NSpace');
  assert.ok(layout);
  assert.ok(layout.props.some(prop => prop.type === 6 && prop.name === 'vertical'));
  assert.equal(binding(layout, 'size'), '16');
  assert.deepEqual(layout.children.filter(node => node.type === 1 && node.tag === 'NFormItem').map(node => attribute(node, 'label')), ['类型', '名称']);
  assert.ok(dialogs[0].children.some(node => node.type === 1 && node.tag === 'template' && node.props.some(prop => prop.type === 7 && prop.name === 'slot' && prop.arg?.content === 'actions')));
});

test('drive loading failure is left aligned across the grid without hiding browsing guidance', () => {
  const source = readFileSync(new URL('../../src/features/files/FilesView.vue', import.meta.url), 'utf8');
  assert.match(source, /<p v-else>选择驱动器开始浏览<\/p>/);
  assert.match(source, /<NEmpty[^>]*description="无法加载驱动器列表"[^>]*class="file-roots-empty"/);
  assert.match(source, /\.file-roots-empty\s*\{[^}]*grid-column: 1 \/ -1;[^}]*align-items: flex-start;[^}]*text-align: left;/);
});

function fixture({ abortSignal = AbortSignal } = {}) {
  const code = loadScriptModule(new URL('../../src/features/files/FilesView.vue', import.meta.url),
    'pageError, loadRoots, rootsLoading, rootsLoaded, roots, browse, browseTarget, resetTargetDirectory, startEdit, loadEdit, closeEdit, editLoaded, editLoading, editTargetModal, confirmEditSave, submitFileOperation, uploadSelectedFiles, confirmDownload, directory, targetDirectory, targetPath, editTarget, editContent, editOriginalLength, uploadFiles, uploadDirectoryPath, uploadItems, downloadTargets, transferKind, requests, loading, submitting, formatTime, selectedEntries, fileSelection, allPagedEntriesSelected, somePagedEntriesSelected, toggleEntrySelection, togglePagedSelection, page, pageSize, fileSearch');
  const requests = [], toasts = [], revokedUrls = [];
  const anchor = { click() {}, remove() {} };
  const safeModeEnabled = vue.ref(false);
  let unmount;
  const context = {
    exports: {}, AbortController, AbortSignal: abortSignal, DOMException, FormData,
    URL: { createObjectURL: () => 'blob:download', revokeObjectURL: url => revokedUrls.push(url) },
    document: { createElement: () => anchor, body: { append() {} } },
    require: name => {
      if (name.endsWith('/useDelayedLoading')) return delayedLoading;
      if (name.endsWith('/useModalState')) return modalStateModule;
      if (name === './useFileSelection') return fileSelection;
      if (name === 'vue') return { ...vue, useId: () => 'files-test', onMounted: () => {}, onBeforeUnmount: callback => { unmount = callback; } };
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
  context.exports.selectedPageCount = context.exports.fileSelection.selectedPageCount;
  return { subject: context.exports, requests, toasts, safeModeEnabled, anchor, revokedUrls, dispose: () => { unmount(); scope.stop(); } };
}
const directory = path => ({ path, entries: [], parentPath: null, isSensitiveSystemPath: false });
const ok = value => ({ ok: true, json: async () => value });

test('initial drive loading has its own pending state and preserves the list during refresh', async () => {
  const f = fixture();
  try {
    const initial = f.subject.loadRoots();
    assert.equal(f.subject.rootsLoading.value, true);
    assert.equal(f.subject.rootsLoaded.value, false);
    assert.equal(f.subject.loading.value, false);
    f.requests[0].resolve(ok([{ path: 'C:\\', name: '系统', kind: 'fixed' }]));
    await initial;
    assert.equal(f.subject.rootsLoading.value, false);
    assert.equal(f.subject.rootsLoaded.value, true);
    const refresh = f.subject.loadRoots();
    assert.equal(f.subject.roots.value.length, 1);
    f.requests[1].reject(new Error('无法连接到 WinPilot'));
    await refresh;
    assert.equal(f.subject.roots.value.length, 1);
    assert.equal(f.subject.rootsLoading.value, false);
    assert.equal(f.subject.pageError.value, '无法连接到 WinPilot');
    assert.equal(f.toasts.length, 0);
  } finally { f.dispose(); }
});

test('file selection follows paths across row replacement, pagination and search', async () => {
  const f = fixture();
  try {
    const s = f.subject;
    const entries = ['alpha', 'beta', 'gamma'].map(name => ({ name, path: `C:\\${name}` }));
    s.directory.value = { ...directory('C:\\'), entries };
    s.pageSize.value = 2;
    await vue.nextTick();
    s.toggleEntrySelection(entries[0], true);
    s.toggleEntrySelection({ ...entries[0] }, true);
    assert.equal(s.selectedEntries.value.length, 1);
    assert.equal(s.selectedPageCount.value, 1);
    assert.equal(s.somePagedEntriesSelected.value, true);
    assert.equal(s.allPagedEntriesSelected.value, false);
    s.directory.value = { ...directory('C:\\'), entries: entries.map(entry => ({ ...entry })) };
    assert.equal(s.selectedPageCount.value, 1);
    s.togglePagedSelection(true);
    assert.equal(s.allPagedEntriesSelected.value, true);
    s.page.value = 2;
    assert.equal(s.selectedPageCount.value, 0);
    s.togglePagedSelection(true);
    assert.equal(s.selectedEntries.value.length, 3);
    s.togglePagedSelection(false);
    assert.deepEqual(Array.from(s.selectedEntries.value, entry => entry.name), ['alpha', 'beta']);
    s.fileSearch.value = 'beta';
    await vue.nextTick();
    assert.equal(s.page.value, 1);
    assert.equal(s.allPagedEntriesSelected.value, true);
    s.togglePagedSelection(false);
    assert.deepEqual(Array.from(s.selectedEntries.value, entry => entry.name), ['alpha']);
    s.toggleEntrySelection({ ...entries[0] }, false);
    assert.equal(s.selectedEntries.value.length, 0);
    assert.equal(s.somePagedEntriesSelected.value, false);
    assert.equal(s.allPagedEntriesSelected.value, false);
  } finally { f.dispose(); }
});

test('file timestamps keep slash-separated local dates with padded fields and 24-hour midnight', () => {
  const f = fixture();
  try {
    assert.equal(f.subject.formatTime('2026-01-09T00:06:00'), '2026/01/09 00:06');
    assert.equal(f.subject.formatTime('2026-10-19T00:46:00'), '2026/10/19 00:46');
    assert.equal(f.subject.formatTime('2026-12-31T23:59:00'), '2026/12/31 23:59');
  } finally { f.dispose(); }
});

test('file pagination clamps after list shrink and stops observing after its scope is disposed', async () => {
  const scope = vue.effectScope();
  const current = vue.ref(directory('C:\\'));
  const selection = scope.run(() => fileSelection.useFileSelection(current));
  try {
    current.value.entries = ['a', 'b', 'c'].map(name => ({ name, path: name }));
    selection.pageSize.value = 1;
    await vue.nextTick();
    selection.page.value = 3;
    assert.equal(selection.pagedEntries.value[0].name, 'c');
    current.value.entries = current.value.entries.slice(0, 1);
    await vue.nextTick();
    assert.equal(selection.page.value, 1);
    assert.equal(selection.pageCount.value, 1);
    scope.stop();
    selection.page.value = 3;
    selection.fileSearch.value = 'missing';
    await vue.nextTick();
    assert.equal(selection.page.value, 3);
  } finally { scope.stop(); }
});

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

test('edit reads block competing entries and saving uses the original target snapshot', async () => {
  const f = fixture();
  try {
    const first = f.subject.startEdit({ path: 'first' });
    await f.subject.startEdit({ path: 'second' });
    assert.equal(f.requests.length, 1);
    f.requests[0].resolve(ok({ content: 'new', length: 3, lastWriteTime: 'time', encoding: 'utf-8' }));
    await first;
    assert.equal(f.subject.editTarget.value.path, 'first');
    assert.equal(f.subject.editContent.value, 'new');
    const save = f.subject.confirmEditSave();
    await f.subject.confirmEditSave();
    assert.equal(f.requests.length, 2);
    f.subject.editTarget.value = undefined;
    f.requests[1].resolve({ ok: true });
    await setImmediate();
    assert.ok(f.requests[2].url.endsWith('path=first'));
    f.requests[2].resolve(ok({ length: 9, lastWriteTime: 'later' }));
    await save;
    assert.equal(f.subject.editTarget.value, undefined);
    assert.equal(f.subject.editOriginalLength.value, 3);
  } finally { f.dispose(); }
});

test('file editor opens after reading and failed reads leave the entry available for retry', async () => {
  const f = fixture();
  try {
    const pending = f.subject.startEdit({ path: 'file', name: 'file' });
    assert.equal(f.subject.editTargetModal.show, false);
    assert.equal(f.subject.editLoading.value, true);
    assert.equal(f.subject.editLoaded.value, false);
    await f.subject.confirmEditSave();
    await f.subject.loadEdit();
    assert.equal(f.requests.length, 1);
    f.requests[0].reject(new Error('读取失败'));
    await pending;
    assert.equal(f.subject.editTargetModal.show, false);
    assert.equal(f.toasts.length, 1);
    assert.equal(f.subject.pageError.value, '');
    const retry = f.subject.startEdit({ path: 'file', name: 'file' });
    f.requests[1].resolve(ok({ content: '', length: 0, lastWriteTime: 'time', encoding: 'utf-8' }));
    await retry;
    assert.equal(f.subject.editLoaded.value, true);
    assert.equal(f.subject.editTargetModal.show, true);
  } finally { f.dispose(); }
});

test('a successful write followed by a failed metadata read reports saved and blocks stale saves', async () => {
  const f = fixture();
  try {
    const opened = f.subject.startEdit({ path: 'file', name: 'file' });
    f.requests[0].resolve(ok({ content: 'old', length: 3, lastWriteTime: 'time', encoding: 'utf-8' }));
    await opened;
    f.subject.editContent.value = 'changed';
    const saved = f.subject.confirmEditSave();
    f.requests[1].resolve({ ok: true });
    await setImmediate();
    f.requests[2].reject(new Error('offline'));
    await saved;
    assert.match(f.toasts.at(-1).text, /文件已保存.*重新打开/);
    assert.equal(f.subject.editContent.value, 'changed');
    assert.equal(f.subject.editLoaded.value, false);
    assert.equal(f.subject.submitting.value, false);
    await f.subject.confirmEditSave();
    assert.equal(f.requests.length, 3);
  } finally { f.dispose(); }
});

test('closing a file editor aborts reading and cannot populate a reopened editor', async () => {
  const f = fixture();
  try {
    const first = f.subject.startEdit({ path: 'first' });
    f.subject.closeEdit();
    assert.equal(f.subject.editTargetModal.show, false);
    assert.equal(f.requests[0].init.signal.aborted, true);
    const second = f.subject.startEdit({ path: 'second' });
    f.requests[0].resolve(ok({ content: 'old' }));
    await first;
    assert.equal(f.subject.editLoaded.value, false);
    assert.equal(f.subject.editContent.value, '');
    f.requests[1].resolve(ok({ content: 'new', length: 3, lastWriteTime: 'time', encoding: 'utf-8' }));
    await second;
    assert.equal(f.subject.editContent.value, 'new');
    assert.equal(f.toasts.length, 0);
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
    assert.equal(JSON.parse(f.requests[0].init.body).directory, 'selected-location');
    assert.equal(f.subject.uploadFiles.value.length, 0);
    assert.equal(f.subject.uploadItems.value[0].status, 'preparing');
    f.requests[0].resolve(ok({ uploadId: 'upload-test' }));
    await setImmediate();
    assert.equal(f.subject.uploadItems.value[0].status, 'uploading');
    assert.equal(f.subject.uploadItems.value[0].percentage, 0);
    f.requests[1].resolve(ok());
    await setImmediate();
    assert.equal(f.subject.uploadItems.value[0].status, 'saving');
    assert.equal(f.subject.uploadItems.value[0].percentage, 100);
    f.requests[2].resolve(ok());
    await pending;
    assert.equal(f.requests.length, 3);
    assert.equal(f.subject.directory.value.path, 'new-location');
    assert.equal(f.subject.uploadItems.value[0].status, 'success');
  } finally { f.dispose(); }
});

test('upload progress counts acknowledged chunks and preserves failures before the next file', async () => {
  const f = fixture();
  try {
    f.subject.uploadDirectoryPath.value = 'selected-location';
    f.subject.uploadFiles.value = [new File([new Uint8Array(1024 * 1024)], 'large.bin'), new File(['x'], 'next.txt')];
    const pending = f.subject.uploadSelectedFiles();
    assert.equal(f.subject.uploadItems.value[1].status, 'waiting');
    f.requests[0].resolve(ok({ uploadId: 'first' }));
    await setImmediate();
    assert.equal(f.requests[1].init.body.size, 512 * 1024);
    assert.equal(f.subject.uploadItems.value[0].percentage, 0);
    f.requests[1].resolve(ok());
    await setImmediate();
    assert.equal(f.subject.uploadItems.value[0].percentage, 50);
    assert.match(f.requests[2].url, /offset=524288$/);
    f.requests[2].reject(new Error('chunk failed'));
    await setImmediate();
    assert.equal(f.requests[3].url, '/api/files/upload/cancel');
    f.requests[3].resolve(ok());
    await setImmediate();
    assert.equal(f.subject.uploadItems.value[0].status, 'failed');
    assert.equal(f.subject.uploadItems.value[0].percentage, 50);
    assert.equal(f.subject.uploadItems.value[0].error, 'chunk failed');
    assert.equal(f.subject.uploadItems.value[1].status, 'preparing');
    f.requests[4].resolve(ok({ uploadId: 'second' }));
    await setImmediate();
    f.requests[5].resolve(ok());
    await setImmediate();
    assert.equal(f.subject.uploadItems.value[1].status, 'saving');
    f.requests[6].resolve(ok());
    await pending;
    assert.equal(f.subject.uploadItems.value[1].status, 'success');
    assert.equal(f.subject.uploadItems.value[1].percentage, 100);
  } finally { f.dispose(); }
});

test('empty uploads complete without sending an empty chunk', async () => {
  const f = fixture();
  try {
    f.subject.uploadDirectoryPath.value = 'selected-location';
    f.subject.uploadFiles.value = [new File([], 'empty.txt')];
    const pending = f.subject.uploadSelectedFiles();
    assert.equal(JSON.parse(f.requests[0].init.body).size, 0);
    f.requests[0].resolve(ok({ uploadId: 'empty' }));
    await setImmediate();
    assert.equal(f.requests[1].url, '/api/files/upload/complete');
    f.requests[1].resolve(ok());
    await pending;
    assert.equal(f.subject.uploadItems.value[0].status, 'success');
    assert.equal(f.subject.uploadItems.value[0].percentage, 100);
    assert.equal(f.requests.length, 2);
  } finally { f.dispose(); }
});

test('completion connection failure is reported without retrying', async () => {
  const f = fixture();
  try {
    f.subject.uploadDirectoryPath.value = 'selected-location';
    f.subject.uploadFiles.value = [new File([], 'empty.txt')];
    const pending = f.subject.uploadSelectedFiles();
    f.requests[0].resolve(ok({ uploadId: 'session' }));
    await setImmediate();
    f.requests[1].reject(new apiErrors.WinPilotConnectionError());
    await setImmediate();
    assert.equal(f.requests[2].url, '/api/files/upload/cancel');
    f.requests[2].resolve(ok());
    await pending;
    assert.equal(f.requests.length, 3);
    assert.equal(f.subject.uploadItems.value[0].status, 'failed');
    assert.equal(f.subject.uploadItems.value[0].error, '无法连接到 WinPilot');
  } finally { f.dispose(); }
});

test('failed upload is shown immediately and cleanup timeout allows the next file', async () => {
  const cleanup = new AbortController();
  const timeouts = [];
  const f = fixture({ abortSignal: { timeout: milliseconds => { timeouts.push(milliseconds); return cleanup.signal; } } });
  try {
    f.subject.uploadDirectoryPath.value = 'selected-location';
    f.subject.uploadFiles.value = [new File(['x'], 'first.txt'), new File(['y'], 'second.txt')];
    const pending = f.subject.uploadSelectedFiles();
    f.requests[0].resolve(ok({ uploadId: 'first' }));
    await setImmediate();
    f.requests[1].reject(new Error('chunk failed'));
    await setImmediate();
    assert.equal(f.subject.uploadItems.value[0].status, 'failed');
    assert.equal(f.subject.uploadItems.value[0].error, 'chunk failed');
    assert.equal(f.requests[2].url, '/api/files/upload/cancel');
    assert.deepEqual(timeouts, [5000]);
    f.requests[2].init.signal.addEventListener('abort', () => f.requests[2].reject(new DOMException('清理超时', 'AbortError')), { once: true });
    cleanup.abort();
    await setImmediate();
    assert.equal(f.requests[3].url, '/api/files/upload/start');
    f.requests[3].resolve(ok({ uploadId: 'second' }));
    await setImmediate();
    f.requests[4].resolve(ok());
    await setImmediate();
    f.requests[5].resolve(ok());
    await pending;
    assert.equal(f.subject.uploadItems.value[0].error, 'chunk failed');
    assert.equal(f.subject.uploadItems.value[1].status, 'success');
    assert.equal(f.toasts.length, 0);
  } finally { f.dispose(); }
});

for (const stage of ['start', 'complete']) {
  test(`upload ${stage} failure keeps the correct progress and never reports success`, async () => {
    const f = fixture();
    try {
      f.subject.uploadDirectoryPath.value = 'selected-location';
      f.subject.uploadFiles.value = [new File(['content'], 'file.txt')];
      const pending = f.subject.uploadSelectedFiles();
      if (stage === 'start') {
        f.requests[0].reject(new Error('start failed'));
      } else {
        f.requests[0].resolve(ok({ uploadId: 'upload-test' }));
        await setImmediate();
        f.requests[1].resolve(ok());
        await setImmediate();
        assert.equal(f.subject.uploadItems.value[0].status, 'saving');
        f.requests[2].reject(new Error('complete failed'));
        await setImmediate();
        assert.equal(f.requests[3].url, '/api/files/upload/cancel');
        f.requests[3].reject(new Error('cleanup failed'));
      }
      await pending;
      const item = f.subject.uploadItems.value[0];
      assert.equal(item.status, 'failed');
      assert.equal(item.percentage, stage === 'start' ? 0 : 100);
      assert.equal(item.error, `${stage} failed`);
      assert.equal(f.subject.requests.size, 0);
      assert.equal(f.requests.length, stage === 'start' ? 1 : 4);
    } finally { f.dispose(); }
  });
}

for (const cancellation of ['unmount', 'safe-mode']) {
  test(`upload ${cancellation} clears progress without late updates or failure messages`, async () => {
    const f = fixture();
    let disposed = false;
    try {
      f.subject.uploadDirectoryPath.value = 'selected-location';
      f.subject.uploadFiles.value = [new File([new Uint8Array(1024 * 1024)], 'large.bin')];
      const pending = f.subject.uploadSelectedFiles();
      f.requests[0].resolve(ok({ uploadId: 'upload-test' }));
      await setImmediate();
      f.requests[1].resolve(ok());
      await setImmediate();
      assert.equal(f.subject.uploadItems.value[0].percentage, 50);
      if (cancellation === 'unmount') {
        f.dispose();
        disposed = true;
      } else {
        f.safeModeEnabled.value = true;
      }
      assert.equal(f.requests[2].init.signal.aborted, true);
      assert.equal(f.subject.uploadItems.value.length, 0);
      f.requests[2].resolve(ok());
      await setImmediate();
      assert.equal(f.requests[3].url, '/api/files/upload/cancel');
      f.requests[3].resolve(ok());
      await pending;
      assert.equal(f.requests.some(request => request.url === '/api/files/upload/complete'), false);
      assert.equal(f.subject.uploadItems.value.length, 0);
      assert.equal(f.subject.requests.size, 0);
      assert.equal(f.toasts.length, 0);
    } finally { if (!disposed) f.dispose(); }
  });
}

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
test('file names reserve line height for descenders while retaining ellipsis', () => {
  const source = readFileSync(new URL('../../src/features/files/FilesView.vue', import.meta.url), 'utf8');
  assert.match(source, /\.file-name-copy\s*\{[^}]*line-height:\s*1\.5;/);
  assert.match(source, /\.file-name strong[^}]*text-overflow:\s*ellipsis;/);
});
