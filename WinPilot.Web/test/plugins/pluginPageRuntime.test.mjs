import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, readdirSync } from 'node:fs';
import ts from 'typescript';
import { preparePluginView, validatePluginView } from '../../src/features/plugins/page/pluginPageRuntime.ts';
import { createPluginPageLifetime } from '../../src/features/plugins/page/pluginPageLifetime.ts';
import * as vue from 'vue';
import * as naiveUI from 'naive-ui';
import { loadPluginUI, pluginUiLoaders } from '../../src/features/plugins/page/pluginPageComponents.ts';

test('shared UI only exposes host imports and the explicit update-page progress component', () => {
  const hostImports = new Set();
  const root = new URL('../../src/', import.meta.url);
  for (const file of readdirSync(root, { recursive: true })) {
    if (!/\.(vue|ts)$/.test(file)) continue;
    const source = readFileSync(new URL(file.replaceAll('\\', '/'), root), 'utf8');
    const script = file.endsWith('.vue') ? source.match(/<script setup lang="ts">([\s\S]*?)<\/script>/)?.[1] ?? '' : source;
    const ast = ts.createSourceFile(file, script, ts.ScriptTarget.Latest, true);
    for (const node of ast.statements) {
      if (!ts.isImportDeclaration(node) || node.moduleSpecifier.text !== 'naive-ui') continue;
      const clause = node.importClause;
      if (clause?.isTypeOnly || !clause?.namedBindings || !ts.isNamedImports(clause.namedBindings)) continue;
      for (const entry of clause.namedBindings.elements) {
        if (!entry.isTypeOnly) hostImports.add((entry.propertyName ?? entry.name).text);
      }
    }
  }
  for (const name of Object.keys(pluginUiLoaders)) {
    if (['AppModal', 'AppTooltip', 'useDelayedLoading', 'withSelectLabels'].includes(name)) continue;
    assert.ok(hostImports.has(name) || name === 'NProgress', `Shared export must be used by host or an explicit plugin page: ${name}`);
    const entry = readFileSync(new URL(`../../src/features/plugins/page/components/${name}.ts`, import.meta.url), 'utf8');
    assert.equal(entry.trim(), `export { ${name} as default } from 'naive-ui';`);
  }
  assert.equal(pluginUiLoaders.NTree, undefined);
  assert.equal(pluginUiLoaders.NDataTable, undefined);
  const bridge = readFileSync(new URL('../../src/features/plugins/page/pluginPageLibraries.ts', import.meta.url), 'utf8');
  assert.doesNotMatch(bridge, /import\s+\*\s+as\s+\w+\s+from\s+['"]naive-ui['"]/);
});

test('host UI exports reference existing implementations without eager imports', async () => {
  for (const [name, expected] of [
    ['AppModal', "export { default } from '../../../../components/AppModal.vue';"],
    ['AppTooltip', "export { default } from '../../../../components/AppTooltip.vue';"],
    ['useDelayedLoading', "export { useDelayedLoading as default } from '../../../../ui/useDelayedLoading.ts';"],
    ['withSelectLabels', "export { withSelectLabels as default } from '../../../../ui/selectLabel.ts';"],
  ]) {
    assert.equal(typeof pluginUiLoaders[name], 'function');
    assert.equal(readFileSync(new URL(`../../src/features/plugins/page/components/${name}.ts`, import.meta.url), 'utf8').trim(), expected);
  }
  const source = readFileSync(new URL('../../src/features/plugins/page/pluginPageComponents.ts', import.meta.url), 'utf8');
  assert.doesNotMatch(source, /^import\s/m);
  const hook = await import('../../src/ui/useDelayedLoading.ts');
  const labels = await import('../../src/ui/selectLabel.ts');
  const unused = pluginUiLoaders.AppTooltip;
  pluginUiLoaders.AppTooltip = () => assert.fail('unrequested host UI must not load');
  try {
    const result = await loadPluginUI(['useDelayedLoading', 'useDelayedLoading', 'withSelectLabels']);
    assert.deepEqual(Object.keys(result), ['useDelayedLoading', 'withSelectLabels']);
    assert.equal(result.useDelayedLoading, hook.useDelayedLoading);
    assert.equal(result.withSelectLabels, labels.withSelectLabels);
  } finally { pluginUiLoaders.AppTooltip = unused; }
});

class Element extends EventTarget {
  get isConnected() { return this.root?.isConnected === true; }
  remove() { this.root.children = this.root.children.filter(child => child !== this); }
}
function fixture() {
  const errors = [];
  const parent = createPluginPageLifetime(error => errors.push(error));
  const root = {
    isConnected: true,
    children: [],
    ownerDocument: { createElement: () => new Element() },
    append(node) { node.root = this; this.children.push(node); },
    remove() { this.isConnected = false; },
  };
  const start = view => preparePluginView(root, view, parent.signal, error => errors.push(error));
  return { root, parent, errors, start };
}

test('only Vue factory declarations and explicit stylesheet URLs are accepted', () => {
  assert.throws(() => validatePluginView({ mount() {} }), /create/);
  for (const styles of [[null], 'style.css', ['']]) {
    assert.throws(() => validatePluginView({ create() {}, styles }), /资源地址/);
  }
  validatePluginView({ create() {}, styles: [new URL('https://example.test/style.css')] });
});

test('page and modal factories receive the exact shared Vue and Naive UI instances', async () => {
  const { parent, start } = fixture();
  let scope;
  const component = { setup: () => () => vue.h(naiveUI.NButton, null, () => '按钮') };
  let controls;
  const runtime = start({ async create(context) {
    scope = context;
    controls = await context.loadUI(['NButton', 'NSelect']);
    return component;
  } });
  assert.equal(await runtime.ready, component);
  assert.equal(scope.vue.ref, vue.ref);
  assert.equal(scope.vue.h, vue.h);
  assert.equal(controls.NButton, naiveUI.NButton);
  assert.equal(controls.NSelect, naiveUI.NSelect);
  assert.deepEqual(Object.keys(controls).sort(), ['NButton', 'NSelect']);
  assert.equal(scope.naiveUI, undefined);
  assert.equal(scope.ui, undefined);
  parent.dispose();
  assert.equal(scope.signal.aborted, true);
  await assert.rejects(scope.loadUI(['NButton']), { name: 'AbortError' });
});

test('UI loading invokes only requested entries, deduplicates names and reuses official components', async () => {
  const original = pluginUiLoaders.NButton;
  const unused = pluginUiLoaders.NInputNumber;
  let calls = 0;
  pluginUiLoaders.NButton = () => { calls++; return original(); };
  pluginUiLoaders.NInputNumber = () => assert.fail('unrequested component must not load');
  try {
    const result = await loadPluginUI(['NButton', 'NButton']);
    assert.equal(calls, 1);
    assert.deepEqual(Object.keys(result), ['NButton']);
    assert.equal(result.NButton, naiveUI.NButton);
    const [a, b] = await Promise.all([loadPluginUI(['NButton']), loadPluginUI(['NButton'])]);
    assert.equal(a.NButton, b.NButton);
    assert.deepEqual(await loadPluginUI([]), {});
  } finally {
    pluginUiLoaders.NButton = original;
    pluginUiLoaders.NInputNumber = unused;
  }
});

test('invalid declarations fail before starting any UI imports', async () => {
  const original = pluginUiLoaders.NButton;
  pluginUiLoaders.NButton = () => assert.fail('invalid requests must not begin loading');
  try {
    for (const names of [null, 'NButton', ['NTree'], ['__proto__'], [1], ['NButton', 'Missing']]) {
      await assert.rejects(loadPluginUI(names), /共享 UI/);
    }
  } finally { pluginUiLoaders.NButton = original; }
});

test('UI loading reports import failures and permits a later attempt', async () => {
  const original = pluginUiLoaders.NButton;
  pluginUiLoaders.NButton = async () => { throw new Error('加载失败'); };
  try { await assert.rejects(loadPluginUI(['NButton']), /加载失败/); }
  finally { pluginUiLoaders.NButton = original; }
  assert.equal((await loadPluginUI(['NButton'])).NButton, naiveUI.NButton);
});

test('closing a view while UI is loading prevents returning the loaded controls', async () => {
  const original = pluginUiLoaders.NButton;
  const controller = new AbortController();
  let finish;
  pluginUiLoaders.NButton = () => new Promise(resolve => { finish = resolve; });
  try {
    const loading = loadPluginUI(['NButton'], controller.signal);
    controller.abort();
    finish(naiveUI.NButton);
    await assert.rejects(loading, { name: 'AbortError' });
  } finally { pluginUiLoaders.NButton = original; }
});

test('ready styles stay active through leave while the runtime is cancelled immediately', async () => {
  const { root, start } = fixture();
  let created = false;
  const runtime = start({ styles: ['style.css', 'style.css'], create() { created = true; return {}; } });
  assert.equal(created, false);
  assert.equal(root.children.length, 1);
  root.children[0].dispatchEvent(new Event('load'));
  await runtime.ready;
  assert.equal(created, true);
  runtime.dispose();
  assert.equal(runtime.signal.aborted, true);
  assert.equal(root.children.length, 1);
  assert.equal(root.children[0].isConnected, true);
  root.remove();
  assert.equal(root.children[0].isConnected, false);
});

test('initialization failure removes every declared stylesheet including ones already loaded', async () => {
  const { root, start } = fixture();
  const runtime = start({ styles: ['first.css', 'second.css'], create() { assert.fail('must not create'); } });
  root.children[0].dispatchEvent(new Event('load'));
  root.children[1].dispatchEvent(new Event('error'));
  await assert.rejects(runtime.ready, /样式加载失败/);
  assert.equal(root.children.length, 0);
});

test('factory failure before presentation removes successfully loaded styles', async () => {
  const { root, start } = fixture();
  const runtime = start({ styles: ['style.css'], create() { throw new Error('初始化失败'); } });
  root.children[0].dispatchEvent(new Event('load'));
  await assert.rejects(runtime.ready, /初始化失败/);
  assert.equal(root.children.length, 0);
});

test('closing during style loading cancels initialization and removes links', async () => {
  const { root, parent, start } = fixture();
  const runtime = start({ styles: ['style.css'], create() { assert.fail('must not create'); } });
  parent.dispose();
  await assert.rejects(runtime.ready, { name: 'AbortError' });
  assert.equal(root.children.length, 0);
});

test('stylesheet errors prevent factory execution', async () => {
  const { root, start } = fixture();
  const runtime = start({ styles: ['style.css'], create() { assert.fail('must not create'); } });
  root.children[0].dispatchEvent(new Event('error'));
  await assert.rejects(runtime.ready, /样式加载失败/);
  assert.equal(root.children.length, 0);
});

test('late factory completion does not mount after parent disposal and cleanup runs once', async () => {
  const { parent, start } = fixture();
  let finish, created;
  const started = new Promise(resolve => { created = resolve; });
  let disposed = 0;
  const runtime = start({ create(scope) {
    scope.onDispose(() => disposed++);
    created();
    return new Promise(resolve => { finish = resolve; });
  } });
  await started;
  parent.dispose();
  finish({});
  assert.equal(await runtime.ready, undefined);
  runtime.dispose();
  assert.equal(disposed, 1);
});

test('factory failure releases resources and isolates cleanup errors', async () => {
  const { errors, start } = fixture();
  let disposed = 0;
  const runtime = start({ create(scope) {
    scope.onDispose(() => disposed++);
    scope.onDispose(() => { throw new Error('清理失败'); });
    throw new Error('初始化失败');
  } });
  await assert.rejects(runtime.ready, /初始化失败/);
  assert.equal(disposed, 1);
  assert.equal(errors.length, 1);
});

test('closed parent never invokes factory and invalid factory results are rejected', async () => {
  const { parent, start } = fixture();
  parent.dispose();
  assert.equal(await start({ create() { assert.fail('must not create'); } }).ready, undefined);
  for (const result of [undefined, null, [], 'template']) {
    const runtime = fixture().start({ create: () => result });
    await assert.rejects(runtime.ready, /返回 Vue 组件/);
    assert.equal(runtime.signal.aborted, true);
  }
});

test('renderer stays within host providers and does not create separate apps or Shadow DOM', () => {
  const source = readFileSync(new URL('../../src/features/plugins/page/PluginPageRenderer.vue', import.meta.url), 'utf8');
  assert.match(source, /<component :is="component" v-if="component"/);
  assert.match(source, /onErrorCaptured/);
  assert.doesNotMatch(source, /attachShadow|createApp|AppPicker|controlStyles/);
});
