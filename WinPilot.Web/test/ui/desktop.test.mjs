import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';

function fixture() {
  const code = loadScriptModule(new URL('../../src/features/desktop/DesktopView.vue', import.meta.url),
    'autoRefreshEnabled, imageUrl, relativeCapturedAt, captureScreenshot, handlePageResume, keyboardEnabled, selectedKeys, keyboardStatus, keyboardError, isSendingKeys, updateSelectedKeys, sendSelectedKeys, imageViewer, openImageViewer, closeImageViewer');
  let now = 100000;
  let timerId = 0;
  let blobId = 0;
  let mounted;
  let unmount;
  const intervals = new Map();
  const timeouts = new Map();
  const listeners = new Set();
  const requests = [];
  const revoked = [];
  const modalCalls = [];
  const toasts = [];
  const safeModeEnabled = vue.ref(false);
  const document = { visibilityState: 'visible', addEventListener: name => listeners.add(name), removeEventListener: name => listeners.delete(name) };
  class Clock extends Date {
    constructor(...args) { super(...(args.length ? args : [now])); }
    static now() { return now; }
  }
  const context = {
    exports: {}, Date: Clock, AbortController, DOMException, document,
    URL: { createObjectURL: () => `blob:${++blobId}`, revokeObjectURL: url => revoked.push(url) },
    window: {
      setInterval: callback => { intervals.set(++timerId, callback); return timerId; },
      clearInterval: id => intervals.delete(id),
      setTimeout: callback => { timeouts.set(++timerId, callback); return timerId; },
      clearTimeout: id => timeouts.delete(id),
      addEventListener: name => listeners.add(name), removeEventListener: name => listeners.delete(name),
    },
    require: name => {
      if (name === 'vue') return { ...vue, onMounted: callback => { mounted = callback; }, onBeforeUnmount: callback => { unmount = callback; } };
      if (name.endsWith('/client')) return {
        apiFetch: (url, init) => new Promise(resolve => requests.push({ url, init, resolve })),
        isAbortError: error => error?.name === 'AbortError', userErrorMessage: error => error.message,
      };
      if (name.endsWith('/safeMode')) return { safeModeEnabled };
      if (name.endsWith('/scrollLock')) return { getPageScroller: () => ({}), lockScroll: () => { modalCalls.push('lock'); return () => modalCalls.push('unlock'); } };
      if (name.endsWith('/modalFocus')) return { manageModalFocus: () => { modalCalls.push('focus'); return () => modalCalls.push('releaseFocus'); } };
      if (name.endsWith('/modalTouch')) return { manageModalTouch: () => { modalCalls.push('touch'); return () => modalCalls.push('releaseTouch'); } };
      if (name.endsWith('/toast')) return { showToast: (...args) => toasts.push(args) };
      return {};
    },
  };
  const scope = vue.effectScope();
  scope.run(() => runInNewContext(code, context));
  mounted();
  const subject = context.exports;
  const capture = async () => {
    const pending = subject.captureScreenshot();
    requests.at(-1).resolve({ ok: true, blob: async () => ({}) });
    await pending;
  };
  return {
    subject, capture, document, intervals, timeouts, requests, revoked, listeners, modalCalls, toasts, safeModeEnabled,
    advance: milliseconds => { now += milliseconds; for (const callback of [...intervals.values()]) callback(); },
    dispose: () => { unmount(); scope.stop(); },
  };
}

test('desktop starts idle, freezes at one hour and restarts the clock after a new screenshot', async () => {
  const f = fixture();
  try {
    assert.equal(f.requests.length, 0);
    assert.equal(f.intervals.size, 0);
    await f.capture();
    assert.equal(f.intervals.size, 1);
    f.advance(3599000);
    assert.equal(f.subject.relativeCapturedAt.value, '59 分钟前');
    f.advance(1000);
    assert.equal(f.subject.relativeCapturedAt.value, '1 小时前');
    assert.equal(f.intervals.size, 0);
    f.advance(3600000);
    f.subject.handlePageResume();
    assert.equal(f.subject.relativeCapturedAt.value, '1 小时前');
    assert.equal(f.intervals.size, 0);
    await f.capture();
    assert.equal(f.subject.relativeCapturedAt.value, '刚刚');
    assert.equal(f.intervals.size, 1);
    assert.deepEqual(f.revoked, ['blob:1']);
  } finally { f.dispose(); }
});

test('hidden desktop stops automatic captures and time updates, then resumes without overlapping requests', async () => {
  const f = fixture();
  try {
    await f.capture();
    f.subject.autoRefreshEnabled.value = true;
    await vue.nextTick();
    assert.equal(f.timeouts.size, 1);
    f.document.visibilityState = 'hidden';
    f.subject.handlePageResume();
    assert.equal(f.timeouts.size, 0);
    assert.equal(f.intervals.size, 0);
    f.document.visibilityState = 'visible';
    f.subject.handlePageResume();
    assert.equal(f.timeouts.size, 1);
    assert.equal(f.intervals.size, 1);
    const pending = f.subject.captureScreenshot();
    await f.subject.captureScreenshot();
    assert.equal(f.requests.length, 2);
    f.requests.at(-1).resolve({ ok: true, blob: async () => ({}) });
    await pending;
  } finally { f.dispose(); }
});

test('keyboard selections cannot change while their send request is pending', async () => {
  const f = fixture();
  try {
    f.subject.keyboardEnabled.value = true;
    f.subject.updateSelectedKeys(['Ctrl', 'C']);
    const pending = f.subject.sendSelectedKeys();
    assert.equal(f.subject.isSendingKeys.value, true);
    f.subject.updateSelectedKeys(['A']);
    assert.deepEqual(JSON.parse(f.requests.at(-1).init.body).keys, ['Ctrl', 'C']);
    assert.equal(f.subject.selectedKeys.value.join(','), 'Ctrl,C');
    f.requests.at(-1).resolve({ ok: true });
    await pending;
    assert.equal(f.subject.selectedKeys.value.length, 0);
    assert.equal(f.subject.isSendingKeys.value, false);
    assert.deepEqual(f.toasts, [['键盘输入已发送', 'success']]);
  } finally { f.dispose(); }
});

test('keyboard send failures show an error toast and inline error', async () => {
  const f = fixture();
  try {
    f.subject.keyboardEnabled.value = true;
    f.subject.updateSelectedKeys(['Enter']);
    const pending = f.subject.sendSelectedKeys();
    f.requests.at(-1).resolve({ ok: false });
    await pending;
    assert.equal(f.subject.keyboardStatus.value, undefined);
    assert.equal(f.subject.keyboardError.value, '键盘输入发送失败');
    assert.equal(f.toasts.length, 1);
    assert.deepEqual(f.toasts[0], ['键盘输入发送失败']);
  } finally { f.dispose(); }
});

test('safe mode clears keyboard state immediately and ignores a late send response', async () => {
  const f = fixture();
  try {
    f.subject.keyboardEnabled.value = true;
    f.subject.updateSelectedKeys(['Ctrl', 'C']);
    const pending = f.subject.sendSelectedKeys();
    const request = f.requests.at(-1);
    f.safeModeEnabled.value = true;
    assert.equal(request.init.signal.aborted, true);
    assert.equal(f.subject.selectedKeys.value.length, 0);
    assert.equal(f.subject.isSendingKeys.value, false);
    f.subject.updateSelectedKeys(['A']);
    assert.equal(f.subject.selectedKeys.value.length, 0);
    f.safeModeEnabled.value = false;
    request.resolve({ ok: true });
    await pending;
    assert.equal(f.subject.keyboardStatus.value, undefined);
    f.subject.updateSelectedKeys(['A']);
    const next = f.subject.sendSelectedKeys();
    f.requests.at(-1).resolve({ ok: true });
    await next;
    assert.equal(f.requests.length, 2);
    assert.equal(f.subject.selectedKeys.value.length, 0);
  } finally { f.dispose(); }
});

test('image preview releases shared modal resources and ignores an opening cancelled before rendering', async () => {
  const f = fixture();
  try {
    f.subject.imageViewer.value = {};
    f.subject.openImageViewer();
    await vue.nextTick();
    await vue.nextTick();
    await setImmediate();
    assert.deepEqual(f.modalCalls, ['lock', 'focus', 'touch']);
    f.subject.closeImageViewer();
    await vue.nextTick();
    assert.deepEqual(f.modalCalls, ['lock', 'focus', 'touch', 'unlock', 'releaseTouch', 'releaseFocus']);
    f.modalCalls.length = 0;
    f.subject.openImageViewer();
    f.subject.closeImageViewer();
    await vue.nextTick();
    assert.deepEqual(f.modalCalls, ['lock', 'unlock']);
  } finally { f.dispose(); }
});

test('desktop disposal aborts requests, releases the last image and removes all timers and listeners', async () => {
  const f = fixture();
  await f.capture();
  const pending = f.subject.captureScreenshot();
  f.dispose();
  assert.equal(f.requests.at(-1).init.signal.aborted, true);
  f.requests.at(-1).resolve({ ok: true, blob: async () => ({}) });
  await pending;
  assert.equal(f.subject.imageUrl.value, undefined);
  assert.deepEqual(f.revoked, ['blob:1']);
  assert.equal(f.intervals.size, 0);
  assert.equal(f.timeouts.size, 0);
  assert.equal(f.listeners.size, 0);
});
