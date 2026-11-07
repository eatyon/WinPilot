import assert from 'node:assert/strict';
import { modalStateModule } from '../support/modalState.mjs';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { compileScript, parse } from '@vue/compiler-sfc';
import ts from 'typescript';
import * as vue from 'vue';
import { elements, attribute, event } from '../support/template.mjs';

test('safe mode uses the user menu and library disabled buttons without a duplicate notice', () => {
  const read = file => readFileSync(new URL(`../../src/${file}`, import.meta.url), 'utf8');
  const app = read('App.vue');
  assert.doesNotMatch(app, /safe-mode-notice|safeModeNoticeDismissed|safeModeMessage/);
  assert.doesNotMatch(app, /safe-mode-indicator/);
  assert.match(app, /<UserMenu\s*\/>/);
  assert.match(app, /<span class="app-brand">/);
  assert.doesNotMatch(app, /RouterLink|to="\/settings\?tab=about"/);
  for (const file of ['features/desktop/DesktopView.vue', 'features/programs/ProgramsView.vue', 'features/tasks/TasksView.vue', 'features/plugins/PluginDetailView.vue', 'features/plugins/PluginInfoView.vue']) {
    assert.doesNotMatch(read(file), /safe-mode-button|safe-mode-lock-icon|safe-mode-brand-lock|name="lock"/);
  }
  assert.doesNotMatch(read('features/security/styles.css'), /safe-mode-button|safe-mode-lock-icon|safe-mode-brand-lock/);
});

test('presentation cleanup retains restricted containers and existing allowed task and tag actions', () => {
  const read = file => readFileSync(new URL(`../../src/features/${file}`, import.meta.url), 'utf8');
  for (const file of ['desktop/DesktopView.vue', 'files/FilesView.vue', 'terminal/TerminalView.vue', 'programs/ProgramsView.vue', 'plugins/PluginDetailView.vue']) {
    assert.match(read(file), /'safe-mode-locked': safeModeEnabled/);
    assert.match(read(file), /:inert="safeModeEnabled"/);
  }
  assert.match(read('settings/SettingsView.vue'), /:inert="settingsReadOnly"/);
  const tasks = read('tasks/TasksView.vue');
  assert.match(tasks, /:disabled="safeModeEnabled"\s+@click="openTaskEditor\(\)"/);
  assert.match(tasks, /<NButton @click="requestRun\(detailTarget\)" type="primary">立即运行/);
  assert.match(tasks, /:disabled="pendingOperations.has\(`toggle:\$\{task.name\}`\)"/);
  const launchButton = elements(read('programs/ProgramsView.vue'), node => node.tag === 'NButton' && event(node, 'click') === 'tagLaunchTargetModal.open(tag)')[0];
  assert.ok(launchButton);
  assert.equal(attribute(launchButton, 'type'), 'default');
  assert.equal(attribute(launchButton, 'attr-type'), 'button');
  assert.match(read('plugins/PluginHeader.vue'), /:disabled="!plugin.canChangeEnabled \|\| changing"/);
});

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
  const container = vue.defineComponent({
    inheritAttrs: false,
    setup: (_, { attrs, slots }) => () => vue.h('div', attrs, slots.default?.()),
  });
  const { descriptor } = parse(readFileSync(new URL('../../src/features/settings/SettingsView.vue', import.meta.url), 'utf8'));
  const script = compileScript(descriptor, { id: 'settings-safe-mode', inlineTemplate: true });
  const code = ts.transpileModule(script.content, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
  }).outputText;
  const context = { exports: {}, require(name) {
    if (name.endsWith('/useModalState')) return modalStateModule;
    if (name.endsWith('/panelEffects')) return { usePanelEffects: () => ({ pageTransitionEnabled: vue.ref(false) }) };
    if (name === 'vue') return vue;
    if (name === 'naive-ui') return { NButton: container, NCard: container, NTab: container, NTabs: container };
    if (name === 'vue-router') return { useRoute: () => route, useRouter: () => ({ push() {} }) };
    if (name.endsWith('/safeMode')) return { safeModeEnabled: enabled };
    if (name.endsWith('/theme')) return { useTheme: () => ({ resolvedTheme: vue.ref('light'), accentColor: vue.ref('blue') }) };
    if (name.endsWith('/NotificationSettings.vue')) return notification;
    if (name.endsWith('.vue')) return placeholder;
    if (name.endsWith('/settingsRequests')) return { createSettingsRequests: () => ({
      fetch: async () => ({ ok: true }), json: async () => ({ port: 9123, lanAccessEnabled: false, startupMode: 'Disabled' }),
      active: true, dispose() {},
    }) };
    if (name.endsWith('/client')) return { isAbortError: () => false };
    if (name.endsWith('/toast')) return { showToast() {} };
    throw new Error('Unexpected import: ' + name);
  } };
  runInNewContext(code, context);
  const node = (text = '') => ({ children: [], parent: null, text, props: {}, style: {} });
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
