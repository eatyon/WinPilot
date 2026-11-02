import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';
import { parse } from '@vue/compiler-sfc';
import { ref, shallowRef, computed } from 'vue';
import { createPluginPageLifetime } from '../../src/features/plugins/page/pluginPageLifetime.ts';
import { loadScriptModule } from '../support/scriptModule.mjs';

function setup(file, bindings, exposed) {
  const source = readFileSync(new URL(`../../src/features/plugins/${file === 'PluginPageView.vue' ? '' : 'page/'}${file}`, import.meta.url), 'utf8');
  const { descriptor } = parse(source);
  const code = ts.transpileModule(descriptor.scriptSetup.content, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext },
    transformers: { before: [context => node => ts.visitNode(node, function visit(child) {
      if (ts.isImportDeclaration(child)) return undefined;
      return ts.visitEachChild(child, visit, context);
    })] },
  }).outputText.replace(/^export \{\};\s*$/m, '');
  const mounted = [];
  const unmounted = [];
  const context = {
    ref, shallowRef, computed,
    onMounted: callback => mounted.push(callback),
    onBeforeUnmount: callback => unmounted.push(callback),
    watch() {}, nextTick: async () => {}, onErrorCaptured() {},
    ...bindings,
  };
  runInNewContext(code + `\nglobalThis.subject = { ${exposed.join(', ')} };`, context);
  return { subject: context.subject, mounted, unmounted };
}

test('page readiness rejects stale sessions and failure resets readiness and releases the current session', async () => {
  const intervals = new Map();
  const requests = [];
  let heartbeatSignal;
  let nextTimer = 0;
  const { subject } = setup('PluginPageView.vue', {
    useRoute: () => ({ params: { pluginId: 'test', featureId: 'page' } }),
    useRouter: () => ({}),
    AbortController,
    window: {
      setInterval(callback) { const id = ++nextTimer; intervals.set(id, callback); return id; },
      clearInterval(id) { intervals.delete(id); },
      setTimeout: () => ++nextTimer,
      clearTimeout() {},
    },
    apiFetch: async (url, options) => {
      if (url.endsWith('/heartbeat')) {
        heartbeatSignal = options.signal;
        return { ok: true, status: 200 };
      }
      return { ok: true, json: async () => ({
        id: 'session', pluginName: 'test', webVersion: 'v1', feature: { mode: 'page', entry: 'index.js' },
      }) };
    },
    fetch: async (url, options) => {
      requests.push({ url, options });
      return { ok: true, status: 200 };
    },
    userErrorMessage: (error, fallback) => error.message || fallback,
    isAbortError: error => error.name === 'AbortError',
  }, ['load', 'handlePageFailed', 'handlePageReady', 'pageReady', 'pageSessionId', 'error']);
  await subject.load();
  assert.equal(subject.pageReady.value, false);
  subject.handlePageReady('old-session');
  assert.equal(subject.pageReady.value, false);
  subject.handlePageReady('session');
  assert.equal(subject.pageReady.value, true);
  assert.equal(intervals.size, 1);
  intervals.values().next().value();
  subject.handlePageFailed('session', '初始化失败');
  subject.handlePageFailed('session', '重复错误');
  assert.equal(intervals.size, 0);
  assert.equal(heartbeatSignal.aborted, true);
  assert.equal(subject.pageSessionId.value, '');
  assert.equal(subject.error.value, '初始化失败');
  assert.equal(subject.pageReady.value, false);
  assert.equal(requests.filter(request => request.options.method === 'DELETE').length, 1);
});

test('a stale page failure cannot close the new session', () => {
  const { subject } = setup('PluginPageView.vue', {
    useRoute: () => ({ params: {} }), useRouter: () => ({}),
    fetch: () => { assert.fail('must not close the active session'); },
  }, ['handlePageFailed', 'pageSessionId', 'error']);
  subject.pageSessionId.value = 'new';
  subject.handlePageFailed('old', '旧页面失败');
  assert.equal(subject.pageSessionId.value, 'new');
  assert.equal(subject.error.value, '');
});

test('suppressed connection messages cannot leave page initialization stuck loading', async () => {
  const { subject } = setup('PluginPageView.vue', {
    useRoute: () => ({ params: { pluginId: 'test', featureId: 'page' } }),
    useRouter: () => ({}),
    AbortController,
    window: { clearInterval() {} },
    apiFetch: async () => { throw new Error('connection lost'); },
    userErrorMessage: () => '',
    isAbortError: () => false,
    fetch: async () => ({ ok: true }),
  }, ['load', 'handlePageFailed', 'pageSessionId', 'error']);
  await subject.load();
  assert.equal(subject.error.value, '无法打开插件功能页');
  subject.pageSessionId.value = 'session';
  subject.handlePageFailed('session', '');
  assert.equal(subject.error.value, '无法加载插件页面');
  assert.equal(subject.pageSessionId.value, '');
});

test('expired sessions rebuild automatically while stale expiry events are ignored', async () => {
  let opened = 0;
  const { subject } = setup('PluginPageView.vue', {
    useRoute: () => ({ params: { pluginId: 'test', featureId: 'page' } }),
    useRouter: () => ({}),
    AbortController,
    window: { clearInterval() {}, setInterval() {} },
    apiFetch: async () => {
      opened++;
      return { ok: true, json: async () => ({ id: 'new', feature: { mode: 'page', entry: 'index.js' } }) };
    },
    userErrorMessage: error => error.message,
    isAbortError: () => false,
    fetch: async () => ({ ok: true }),
  }, ['handleSessionExpired', 'handlePageFailed', 'pageSessionId', 'error']);
  subject.pageSessionId.value = 'session';
  subject.handleSessionExpired('old');
  assert.equal(opened, 0);
  subject.handleSessionExpired('session');
  subject.handleSessionExpired('session');
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(opened, 1);
  assert.equal(subject.pageSessionId.value, 'new');
  assert.equal(subject.error.value, '');
  subject.handlePageFailed('new', '模块失败');
  assert.equal(subject.error.value, '模块失败');
  assert.equal(opened, 1);
});

test('foreground checks keep valid sessions and rebuild only on confirmed expiry', async () => {
  for (const status of [200, 404, 401, 500, 'network-error']) {
    let checks = 0;
    let opened = 0;
    const document = { visibilityState: 'hidden' };
    const { subject } = setup('PluginPageView.vue', {
      useRoute: () => ({ params: { pluginId: 'test', featureId: 'page' } }),
      useRouter: () => ({}), AbortController, document,
      window: { clearInterval() {}, setInterval() {}, setTimeout() {}, clearTimeout() {} },
      fetch: async () => ({ ok: true }),
      apiFetch: async (url) => {
        if (url.endsWith('/heartbeat')) {
          checks++;
          if (status === 'network-error') throw new Error('offline');
          return { status };
        }
        opened++;
        return { ok: true, json: async () => ({ id: 'new', feature: { mode: 'page', entry: 'index.js' } }) };
      },
      userErrorMessage: error => error.message, isAbortError: () => false,
    }, ['handleVisibilityChange', 'pageSessionId']);
    subject.pageSessionId.value = 'session';
    subject.handleVisibilityChange();
    assert.equal(checks, 0);
    document.visibilityState = 'visible';
    subject.handleVisibilityChange();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(checks, 1);
    assert.equal(opened, status === 404 ? 1 : 0);
    assert.equal(subject.pageSessionId.value, status === 404 ? 'new' : 'session');
  }
});

test('recovery failure is reported without a retry loop', async () => {
  let opened = 0;
  const { subject } = setup('PluginPageView.vue', {
    useRoute: () => ({ params: {} }), useRouter: () => ({}), AbortController,
    window: { clearInterval() {} },
    apiFetch: async () => { opened++; throw new Error('恢复失败'); },
    userErrorMessage: error => error.message, isAbortError: () => false,
  }, ['handleSessionExpired', 'pageSessionId', 'error']);
  subject.pageSessionId.value = 'session';
  subject.handleSessionExpired('session');
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(opened, 1);
  assert.equal(subject.error.value, '恢复失败');
  assert.equal(subject.pageSessionId.value, '');
});

test('foreground listener is removed and late heartbeats cannot reopen an unmounted page', async () => {
  const listeners = new Map();
  let finishHeartbeat;
  let opened = 0;
  const { subject, mounted, unmounted } = setup('PluginPageView.vue', {
    useRoute: () => ({ params: { pluginId: 'test', featureId: 'page' } }),
    useRouter: () => ({ currentRoute: { value: { path: '/home' } } }),
    AbortController,
    document: {
      addEventListener: (type, callback) => listeners.set(type, callback),
      removeEventListener: type => listeners.delete(type),
    },
    sessionStorage: { getItem: () => null },
    window: { clearInterval() {}, setInterval() {}, setTimeout() {}, clearTimeout() {} },
    apiFetch: async (url) => {
      if (url.endsWith('/heartbeat')) return new Promise(resolve => finishHeartbeat = resolve);
      opened++;
      return { ok: true, json: async () => ({ id: 'session', feature: { mode: 'page', entry: 'index.js' } }) };
    },
    fetch: async () => ({ ok: true }),
    userErrorMessage: error => error.message, isAbortError: () => false,
  }, ['checkSession']);
  mounted[0]();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(listeners.size, 1);
  subject.checkSession('session');
  unmounted[0]();
  finishHeartbeat({ status: 404 });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(listeners.size, 0);
  assert.equal(opened, 1);
});

test('an unauthorized plugin heartbeat uses the shared login redirect without rebuilding the plugin session', async () => {
  const requests = [], navigations = [];
  const location = new URL('http://127.0.0.1:5186/plugins/test/features/page');
  location.assign = path => navigations.push(path);
  const client = {
    exports: {}, URL, DOMException,
    window: { location },
    fetch: async (url, options) => {
      requests.push({ url, options });
      return new Response(null, { status: 401 });
    },
  };
  runInNewContext(loadScriptModule(new URL('../../src/api/client.ts', import.meta.url)), client);
  const { subject } = setup('PluginPageView.vue', {
    useRoute: () => ({ params: { pluginId: 'test', featureId: 'page' } }),
    useRouter: () => ({}), AbortController,
    window: { setTimeout() {}, clearTimeout() {} },
    apiFetch: client.exports.apiFetch,
  }, ['checkSession', 'pageSessionId']);
  subject.pageSessionId.value = 'session';
  subject.checkSession('session');
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(requests.length, 1);
  assert.equal(requests[0].url, '/api/plugin-page-sessions/session/heartbeat');
  assert.equal(requests[0].options.method, 'POST');
  assert.deepEqual(navigations, ['/login']);
  assert.equal(subject.pageSessionId.value, 'session');
});

test('host initialization failure remains visible when connection text is suppressed', () => {
  const events = [];
  const { subject } = setup('PluginPageHost.vue', {
    defineProps: () => ({ sessionId: 'session' }),
    defineEmits: () => (...args) => events.push(args),
    createPluginPageLifetime,
    userErrorMessage: () => '',
    isAbortError: () => false,
    console,
  }, ['handleFailure', 'lifetime']);
  subject.handleFailure(new Error('connection lost'));
  assert.equal(subject.lifetime.signal.aborted, true);
  assert.deepEqual(events, [['failed', 'session', '无法加载插件页面']]);
});

test('host failure disposes page resources and reports the captured session once', () => {
  const events = [];
  const { subject } = setup('PluginPageHost.vue', {
    defineProps: () => ({ sessionId: 'session' }),
    defineEmits: () => (...args) => events.push(args),
    createPluginPageLifetime,
    userErrorMessage: (error, fallback) => error.message || fallback,
    isAbortError: error => error.name === 'AbortError',
    console,
  }, ['handleFailure', 'lifetime']);
  let disposed = 0;
  subject.lifetime.onDispose(() => disposed++);
  subject.handleFailure(new Error('入口加载失败'));
  subject.handleFailure(new Error('迟到错误'));
  assert.equal(disposed, 1);
  assert.equal(subject.lifetime.signal.aborted, true);
  assert.deepEqual(events, [['failed', 'session', '入口加载失败']]);
});

test('renderer reports initialization cancellation unless the parent actually closed', async () => {
  for (const closed of [false, true]) {
    const events = [];
    const parent = new AbortController();
    if (closed) parent.abort();
    const { subject, mounted } = setup('PluginPageRenderer.vue', {
      defineProps: () => ({ signal: parent.signal, view: {} }),
      defineEmits: () => (...args) => events.push(args),
      preparePluginView: () => ({
        signal: parent.signal,
        ready: Promise.reject(new DOMException('初始化已取消', 'AbortError')),
        dispose() {},
      }),
      userErrorMessage: error => error.message,
      console,
    }, ['container']);
    subject.container.value = { attachShadow: () => ({}) };
    await mounted[0]();
    assert.equal(events.length, closed ? 0 : 1);
    if (!closed) assert.equal(events[0][0], 'failed');
  }
});

test('host shares UI without defining modal behavior or compatibility interfaces', () => {
  const host = readFileSync(new URL('../../src/features/plugins/page/PluginPageHost.vue', import.meta.url), 'utf8');
  const types = readFileSync(new URL('../../src/features/plugins/page/pluginPageTypes.ts', import.meta.url), 'utf8');
  assert.doesNotMatch(host, /PluginPageModal|modalStack|handleDocumentKeydown|openModal:|confirm:|notify:/);
  assert.doesNotMatch(types, /PluginModal|PluginConfirmation|openModal:|confirm:|notify:/);
  assert.match(host, /\.\.\.scope/);
  assert.match(types, /loadUI: PluginUILoader/);
});

test('renderer captures component errors, disposes resources and removes the component', async () => {
  const parent = new AbortController();
  const runtime = new AbortController();
  const events = [];
  let capture;
  const component = { render() {} };
  const { subject, mounted } = setup('PluginPageRenderer.vue', {
    defineProps: () => ({ signal: parent.signal, view: {} }),
    defineEmits: () => (...args) => events.push(args),
    onErrorCaptured: callback => { capture = callback; },
    preparePluginView: () => ({ signal: runtime.signal, ready: Promise.resolve(component), dispose: () => runtime.abort() }),
    console,
  }, ['container', 'component']);
  subject.container.value = {};
  await mounted[0]();
  assert.equal(subject.component.value, component);
  assert.equal(events[0][0], 'ready');
  const error = new Error('组件渲染失败');
  assert.equal(capture(error), false);
  assert.equal(runtime.signal.aborted, true);
  assert.equal(subject.component.value, undefined);
  assert.deepEqual(events[1], ['failed', error]);
});

test('renderer never mounts a prepared component after cancellation', async () => {
  const runtime = new AbortController();
  const events = [];
  const { subject, mounted } = setup('PluginPageRenderer.vue', {
    defineProps: () => ({ signal: runtime.signal, view: {} }),
    defineEmits: () => (...args) => events.push(args),
    preparePluginView: () => ({ signal: runtime.signal, ready: Promise.resolve({}), dispose() {} }),
    console,
  }, ['container', 'component']);
  subject.container.value = {};
  const mounting = mounted[0]();
  runtime.abort();
  await mounting;
  assert.equal(subject.component.value, undefined);
  assert.equal(events.length, 0);
});
