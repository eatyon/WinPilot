import { readStylesheet, readFeatureStyles } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';
import * as keyboardLayout from '../../src/ui/keyboardLayout.ts';
import * as refreshTiming from '../../src/ui/refreshTiming.ts';

test('desktop shares the global content layout and refreshes existing images without a loading placeholder', () => {
  const layout = readStylesheet(new URL('../../src/styles/layout.css', import.meta.url));
  assert.match(layout, /\.page-content \{ width: 100%; padding: clamp\(16px, 3vw, 24px\); \}/);
  assert.doesNotMatch(layout, /\.page-content:has\(\.desktop-viewer\)/);
  const source = readFileSync(new URL('../../src/features/desktop/DesktopView.vue', import.meta.url), 'utf8');
  assert.match(source, /<AppLoading v-if="isCapturing && !imageUrl"/);
  assert.doesNotMatch(source, /desktop-frame-error/);
  assert.doesNotMatch(source, /desktop-frame-pending/);
  const css = readFeatureStyles();
  assert.match(source, /<NCard\s+class="desktop-frame"/);
  assert.match(source, /content-class="desktop-frame-content"\s+:content-style="\{ padding: 0 \}"/);
  assert.doesNotMatch(css, /\.desktop-frame\s*\{[^}]*transition:/);
  assert.match(css, /\.desktop-frame:not\(:has\(img\)\) \{ background: color-mix\(in srgb, var\(--canvas\) 90%, var\(--surface\) 10%\); \}/);
  assert.match(css, /@media \(min-width: 768px\)\s*\{\s*\.desktop-frame:not\(:has\(img\)\) \{ min-height: 0; aspect-ratio: 16 \/ 9; \}/);
});

test('desktop image preview uses the public library component without legacy viewer styles', () => {
  const source = readFileSync(new URL('../../src/features/desktop/DesktopView.vue', import.meta.url), 'utf8');
  assert.match(source, /<NImagePreview.*?:show="isImageViewerOpen".*?@close="closeImageViewer"/s);
  assert.match(source, /<button\s+v-if="imageUrl"\s+class="desktop-preview-button"\s+type="button"/);
  assert.doesNotMatch(source, /tabindex="-1"/);
  assert.doesNotMatch(source, /imageViewerScale|imageViewerPointers|@pointerdown|desktop-image-viewer/);
  const css = readFeatureStyles();
  assert.doesNotMatch(css, /desktop-image-viewer/);
  assert.doesNotMatch(css, /desktop-preview-button:focus-visible/);
  const previewCss = readStylesheet(new URL('../../src/features/desktop/DesktopView.vue', import.meta.url));
  assert.match(previewCss, /\.n-image-preview-toolbar \.n-base-icon \{ box-sizing: content-box; \}/);
});

function fixture() {
  const code = loadScriptModule(new URL('../../src/features/desktop/DesktopView.vue', import.meta.url),
    'autoRefreshEnabled, imageUrl, relativeCapturedAt, captureScreenshot, handlePageResume, keyboardEnabled, selectedKeys, isSendingKeys, updateSelectedKeys, sendSelectedKeys, imageViewerToolbar, isImageViewerOpen, renderImageToolbar, openImageViewer, closeImageViewer');
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
      if (name.endsWith('/refreshTiming')) return refreshTiming;
      if (name.endsWith('/AppTooltip.vue')) return { name: 'AppTooltip' };
      if (name === 'naive-ui') return { NButton: { name: 'NButton' } };
      if (name.endsWith('/keyboardLayout')) return keyboardLayout;
      if (name === 'vue') return { ...vue, onMounted: callback => { mounted = callback; }, onBeforeUnmount: callback => { unmount = callback; } };
      if (name.endsWith('/client')) return {
        apiFetch: (url, init) => new Promise(resolve => requests.push({ url, init, resolve })),
        isAbortError: error => error?.name === 'AbortError', userErrorMessage: error => error.message,
      };
      if (name.endsWith('/safeMode')) return { safeModeEnabled };
      if (name.endsWith('/scrollLock')) return { getPageScroller: () => ({}), lockScroll: () => { modalCalls.push('lock'); return () => modalCalls.push('unlock'); } };
      if (name.endsWith('/modalFocus')) return { manageModalFocus: () => { modalCalls.push('focus'); return () => modalCalls.push('releaseFocus'); } };
      if (name.endsWith('/modalTouch')) return { manageModalTouch: () => { modalCalls.push('touch'); return () => modalCalls.push('releaseTouch'); } };
      if (name.endsWith('/imagePreviewTouch')) return { bindImagePreviewTouchDrag: () => { modalCalls.push('drag'); return () => modalCalls.push('releaseDrag'); } };
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

test('preview toolbar reuses library buttons and invokes each image action only once', () => {
  const f = fixture();
  try {
    let clicks = 0;
    const names = ['rotateClockwise', 'rotateCounterclockwise', 'zoomOut', 'zoomIn', 'resizeToOriginalSize', 'download', 'close', 'prev', 'next'];
    const nodes = Object.fromEntries(names.map(name => [name, vue.h('i', { onClick: () => clicks++ }, name)]));
    const toolbar = f.subject.renderImageToolbar({ nodes });
    assert.deepEqual(Array.from(toolbar.children, node => node.props.text), ['顺时针旋转90度', '缩小', '放大', '关闭大图']);
    for (const hint of toolbar.children) {
      assert.equal(hint.type.name, 'AppTooltip');
      const node = hint.children.default();
      assert.equal(node.type.name, 'NButton');
      assert.equal(node.props.title, undefined);
      assert.equal(node.props['aria-label'], hint.props.text);
      assert.equal(node.props.quaternary, true);
      assert.equal(node.props.class, 'app-icon-button');
      assert.equal(node.children.icon().props.onClick, undefined);
      assert.equal(node.children.icon().props['aria-hidden'], true);
      assert.equal(node.props.onKeydown, undefined);
      node.props.onClick();
    }
    assert.equal(clicks, 4);
  } finally { f.dispose(); }
});

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

test('empty keyboard selections show a toast without sending a request', async () => {
  const f = fixture();
  try {
    f.subject.keyboardEnabled.value = true;
    await f.subject.sendSelectedKeys();
    assert.deepEqual(f.toasts, [['请选择至少一个按键']]);
    assert.equal(f.requests.length, 0);
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

test('desktop simulation displays and sends the fixed modifier order', async () => {
  const f = fixture();
  try {
    f.subject.keyboardEnabled.value = true;
    f.subject.updateSelectedKeys(['Shift', 'Ctrl', 'Win']);
    assert.deepEqual([...f.subject.selectedKeys.value], ['Win', 'Ctrl', 'Shift']);
    const pending = f.subject.sendSelectedKeys();
    assert.deepEqual(JSON.parse(f.requests.at(-1).init.body).keys, ['Win', 'Ctrl', 'Shift']);
    f.requests.at(-1).resolve({ ok: true });
    await pending;
  } finally { f.dispose(); }
});

test('keyboard send failures show only an error toast', async () => {
  const f = fixture();
  try {
    f.subject.keyboardEnabled.value = true;
    f.subject.updateSelectedKeys(['Enter']);
    const pending = f.subject.sendSelectedKeys();
    f.requests.at(-1).resolve({ ok: false });
    await pending;
    assert.equal(f.toasts.length, 1);
    assert.deepEqual(f.toasts[0], ['键盘输入发送失败']);
    assert.deepEqual([...f.subject.selectedKeys.value], ['Enter']);
    assert.equal(f.subject.isSendingKeys.value, false);
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
    assert.equal(f.toasts.length, 0);
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
    f.subject.imageViewerToolbar.value = { setAttribute() {}, querySelector() { return null; }, closest() { return this; } };
    f.subject.openImageViewer();
    await vue.nextTick();
    await vue.nextTick();
    await setImmediate();
    assert.deepEqual(f.modalCalls, ['lock', 'focus', 'touch', 'drag']);
    f.subject.closeImageViewer();
    await vue.nextTick();
    assert.deepEqual(f.modalCalls, ['lock', 'focus', 'touch', 'drag', 'unlock', 'releaseDrag', 'releaseTouch', 'releaseFocus']);
    f.modalCalls.length = 0;
    f.subject.openImageViewer();
    f.subject.closeImageViewer();
    await vue.nextTick();
    assert.deepEqual(f.modalCalls, ['lock', 'unlock']);
    f.modalCalls.length = 0;
    f.subject.imageViewerToolbar.value = undefined;
    f.subject.openImageViewer();
    await vue.nextTick();
    assert.deepEqual(f.modalCalls, ['lock']);
    f.subject.imageViewerToolbar.value = { setAttribute() {}, querySelector() { return null; }, closest() { return this; } };
    await vue.nextTick();
    await setImmediate();
    assert.deepEqual(f.modalCalls, ['lock', 'focus', 'touch', 'drag']);
    f.subject.closeImageViewer();
    await vue.nextTick();
    assert.deepEqual(f.modalCalls, ['lock', 'focus', 'touch', 'drag', 'unlock', 'releaseDrag', 'releaseTouch', 'releaseFocus']);
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
