import { createTestRenderer } from '../support/renderer.mjs';
import { loadComponentModule } from '../support/scriptModule.mjs';
import { readStylesheet } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { createMemoryHistory, createRouter } from 'vue-router';
import { startNavigation, finishNavigation, navigationStatus } from '../../src/ui/navigationProgress.ts';
import * as vue from 'vue';
import { binding, elements, event } from '../support/template.mjs';

test('back-to-top reuses the library and listens to the shared content scroller', () => {
  const source = readFileSync(new URL('../../src/App.vue', import.meta.url), 'utf8');
  assert.match(source, /<NBackTop\s+:listen-to="getPageScroller"\s+:right="isDesktop \? 100 : 50"\s+:bottom="isDesktop \? 40 : 20"/);
  assert.match(source, /role="button"\s+tabindex="0"\s+aria-label="回到顶部"/);
  assert.match(source, /@keydown.enter.prevent=/);
  assert.match(source, /@keydown.space.prevent=/);
});

test('navigation no longer probes server health or blocks on global offline state', () => {
  const source = readFileSync(new URL('../../src/router/index.ts', import.meta.url), 'utf8');
  assert.doesNotMatch(source, /\/health|connectionStatus|loadedRoutePaths|apiFetch/);
  let beforeEach;
  const navigations = [];
  runInNewContext(source.match(/router\.beforeEach\([\s\S]*?\n\}\)/)[0], {
    router: { beforeEach: handler => { beforeEach = handler; } },
    startNavigation: target => navigations.push(target),
  });
  const target = { path: '/home' };
  assert.equal(beforeEach(target), undefined);
  assert.deepEqual(navigations, [target]);
});

test('browser title follows successful navigation, falls back without metadata and ignores failed navigation', () => {
  const source = readFileSync(new URL('../../src/router/index.ts', import.meta.url), 'utf8');
  const document = { title: 'WinPilot' };
  let afterEach;
  runInNewContext(source.match(/router\.afterEach\([\s\S]*?\n\}\)/)[0], {
    router: { afterEach: handler => { afterEach = handler; } },
    document, finishNavigation() {},
  });
  for (const title of ['首页', '桌面', '程序', '任务', '插件', '终端', '文件', '日志', '设置', '登录']) {
    afterEach({ meta: { title }, matched: [] }, {}, undefined);
    assert.equal(document.title, `${title} - WinPilot`);
  }
  afterEach({ meta: { title: '桌面' }, matched: [] }, {}, new Error('blocked'));
  assert.equal(document.title, '登录 - WinPilot');
  for (const title of [undefined, '', ' ', {}]) {
    afterEach({ meta: { title }, matched: [] }, {}, undefined);
    assert.equal(document.title, 'WinPilot');
  }
});

test('lazy navigation retains the current route until its code is ready and settles loading on failure', async () => {
  const page = { render: () => null };
  let release;
  const router = createRouter({ history: createMemoryHistory(), routes: [
    { path: '/first', component: page },
    { path: '/lazy', component: () => new Promise(resolve => { release = resolve; }) },
    { path: '/blocked', component: page },
    { path: '/failed', component: () => Promise.reject(new Error('chunk unavailable')) },
  ] });
  router.beforeEach(to => { startNavigation(to); return to.path !== '/blocked'; });
  router.afterEach(to => finishNavigation(to));
  router.onError((_, to) => finishNavigation(to, true));
  await router.push('/first');
  const navigation = router.push('/lazy');
  while (!release) await Promise.resolve();
  assert.equal(navigationStatus.value, 'loading');
  assert.equal(router.currentRoute.value.path, '/first');
  release(page);
  await navigation;
  assert.equal(navigationStatus.value, 'idle');
  assert.equal(router.currentRoute.value.path, '/lazy');
  await router.push('/blocked');
  assert.equal(navigationStatus.value, 'idle');
  assert.equal(router.currentRoute.value.path, '/lazy');
  await assert.rejects(router.push('/failed'), /chunk unavailable/);
  assert.equal(navigationStatus.value, 'error');
});

test('a superseded navigation cannot finish the newer loading indicator', () => {
  const first = {}, second = {};
  startNavigation(first);
  startNavigation(second);
  finishNavigation(first, true);
  assert.equal(navigationStatus.value, 'loading');
  finishNavigation(second);
  assert.equal(navigationStatus.value, 'idle');
});

test('page transitions keep a stable page branch when toggled and reset incoming scroll', () => {
  const view = readFileSync(new URL('../../src/components/AppPageView.vue', import.meta.url), 'utf8');
  const transitions = elements(view, node => node.tag === 'Transition');
  assert.equal(transitions.length, 1);
  assert.equal(binding(transitions[0], 'css'), 'pageTransitionEnabled');
  assert.equal(binding(transitions[0], 'mode'), "pageTransitionEnabled ? 'out-in' : undefined");
  assert.equal(binding(transitions[0], 'name'), '`page-${pageTransitionEffect}`');
  assert.equal(event(transitions[0], 'before-enter'), 'resetPageScroll');
  assert.match(view, /:key="route.path"/);
  assert.doesNotMatch(view, /route.fullPath|KeepAlive/);
  assert.equal(elements(view, node => node.tag === 'component' && binding(node, 'is') === 'Component').length, 1);
  assert.doesNotMatch(view, /v-else-if|v-if="pageTransitionEnabled"/);
  const css = readStylesheet(new URL('../../src/styles/transitions.css', import.meta.url));
  assert.match(css, /\.page-fade-enter-active, \.page-fade-leave-active \{ transition: opacity 300ms ease;/);
  assert.match(css, /\.page-fade-enter-from, \.page-fade-leave-to \{ opacity: 0;/);
});

test('changing page animation preferences preserves the mounted page and its state', async () => {
  const enabled = vue.ref(true), effect = vue.ref('fade');
  const route = vue.reactive({ path: '/first' });
  const scroller = { scrollTop: 0 };
  let mounted = 0, unmounted = 0, draft;
  const page = vue.defineComponent({ setup() {
    mounted++;
    draft = vue.ref('');
    const value = draft;
    vue.onUnmounted(() => unmounted++);
    return () => vue.h('p', value.value);
  } });
  const routerView = vue.defineComponent({ setup(_, { slots }) {
    return () => slots.default({ Component: page, route });
  } });
  const context = { exports: {}, require(name) {
    if (name === 'vue') return { ...vue, Transition: vue.BaseTransition };
    if (name === 'vue-router') return { RouterView: routerView };
    if (name.endsWith('/panelEffects')) return { usePanelEffects: () => ({ pageTransitionEnabled: enabled, pageTransitionEffect: effect }) };
    if (name.endsWith('/scrollLock')) return { getPageScroller: () => scroller };
    throw new Error(`Unexpected import: ${name}`);
  } };
  runInNewContext(loadComponentModule(new URL('../../src/components/AppPageView.vue', import.meta.url)), context);
  const { renderer, root } = createTestRenderer();
  const app = renderer.createApp(context.exports.default);
  app.mount(root);
  try {
    draft.value = 'unsaved';
    scroller.scrollTop = 150;
    for (const value of [false, true, false]) {
      enabled.value = value;
      effect.value = value ? 'slide' : 'fade';
      await vue.nextTick();
      assert.equal(mounted, 1);
      assert.equal(unmounted, 0);
      assert.equal(draft.value, 'unsaved');
      assert.equal(scroller.scrollTop, 150);
    }
    route.path = '/second';
    await vue.nextTick();
    assert.equal(mounted, 2);
    assert.equal(unmounted, 1);
    assert.equal(scroller.scrollTop, 0);
  } finally { app.unmount(); }
});
