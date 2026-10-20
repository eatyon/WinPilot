import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { compileScript, parse } from '@vue/compiler-sfc';
import ts from 'typescript';
import * as vue from 'vue';

test('safe mode keeps notification content inert and recreates it on protection changes', async () => {
  const enabled = vue.ref(true);
  const route = vue.reactive({ query: { tab: 'notification' } });
  let mounted = 0, unmounted = 0;
  const notification = vue.defineComponent({
    setup() {
      vue.onMounted(() => mounted++);
      vue.onBeforeUnmount(() => unmounted++);
      return () => vue.h('div', { 'data-test': 'notification' }, '渠道列表');
    },
  });
  const placeholder = vue.defineComponent({ setup: () => () => vue.h('div') });
  const { descriptor } = parse(readFileSync(new URL('../../src/features/settings/SettingsView.vue', import.meta.url), 'utf8'));
  const script = compileScript(descriptor, { id: 'settings-safe-mode', inlineTemplate: true });
  const code = ts.transpileModule(script.content, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
  }).outputText;
  const context = { exports: {}, require(name) {
    if (name === 'vue') return vue;
    if (name === 'vue-router') return { useRoute: () => route, useRouter: () => ({ push() {} }) };
    if (name.endsWith('/safeMode')) return { safeModeEnabled: enabled };
    if (name.endsWith('/NotificationSettings.vue')) return notification;
    if (name.endsWith('.vue')) return placeholder;
    if (name.endsWith('/SettingsRequests')) return { createSettingsRequests: () => ({
      fetch: async () => ({ ok: true }), json: async () => ({ port: 9123, lanAccessEnabled: false, startupMode: 'Disabled' }),
      active: true, dispose() {},
    }) };
    if (name.endsWith('/client')) return { isAbortError: () => false };
    if (name.endsWith('/toast')) return { showToast() {} };
    throw new Error('Unexpected import: ' + name);
  } };
  runInNewContext(code, context);
  const node = (text = '') => ({ children: [], parent: null, text, props: {} });
  const remove = child => {
    if (child.parent) child.parent.children.splice(child.parent.children.indexOf(child), 1);
    child.parent = null;
  };
  const renderer = vue.createRenderer({
    createElement: () => node(), createText: node, createComment: node,
    setText: (node, text) => { node.text = text; },
    setElementText: (node, text) => { node.text = text; },
    patchProp(node, key, previous, value) { node.props[key] = value; },
    insert(child, parent, anchor) {
      remove(child);
      parent.children.splice(anchor ? parent.children.indexOf(anchor) : parent.children.length, 0, child);
      child.parent = parent;
    },
    remove, parentNode: node => node.parent,
    nextSibling: node => node.parent?.children[node.parent.children.indexOf(node) + 1] ?? null,
  });
  const app = renderer.createApp(context.exports.default);
  const root = node();
  const find = (node, predicate) => predicate(node) ? node : node.children.map(child => find(child, predicate)).find(Boolean);
  const assertVisibleAndLocked = locked => {
    assert.ok(find(root, node => node.props['data-test'] === 'notification'));
    assert.equal(Boolean(find(root, node => node.props.inert === true)), locked);
  };
  app.mount(root);
  try {
    await vue.nextTick();
    assert.equal(mounted, 1);
    assertVisibleAndLocked(true);
    enabled.value = false;
    await vue.nextTick();
    assert.equal(mounted, 2);
    assert.equal(unmounted, 1);
    assertVisibleAndLocked(false);
    enabled.value = true;
    await vue.nextTick();
    assert.equal(mounted, 3);
    assert.equal(unmounted, 2);
    assertVisibleAndLocked(true);
    enabled.value = false;
    await vue.nextTick();
    assert.equal(mounted, 4);
    assert.equal(unmounted, 3);
    assertVisibleAndLocked(false);
    route.query.tab = 'general';
    await vue.nextTick();
    assert.equal(unmounted, 4);
    enabled.value = true;
    await vue.nextTick();
    route.query.tab = 'notification';
    await vue.nextTick();
    assert.equal(mounted, 5);
    assertVisibleAndLocked(true);
  } finally { app.unmount(); }
});
