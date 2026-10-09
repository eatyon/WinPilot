import assert from 'node:assert/strict';
import test from 'node:test';
import { mountPluginView, validatePluginView } from '../../src/features/plugins/page/PluginPageMount.ts';
import { createPluginPageLifetime } from '../../src/features/plugins/page/PluginPageLifetime.ts';

class Element extends EventTarget {
  textContent = '';
  href = '';
}
function fixture() {
  const errors = [];
  const parent = createPluginPageLifetime(error => errors.push(error));
  const root = {
    children: [],
    ownerDocument: { createElement: () => new Element() },
    append(node) { this.children.push(node); },
    replaceChildren() { this.children = []; },
  };
  const start = view => mountPluginView(root, view, parent.signal, error => errors.push(error));
  return { root, parent, errors, start };
}
const settle = () => new Promise(resolve => setImmediate(resolve));

test('page and modal base styles suppress touch highlights on interactive elements only', async () => {
  const { root, start } = fixture();
  const runtime = start({ mount() {} });
  await runtime.ready;
  const css = root.children[0].textContent;
  const rule = css.match(/([^{}]+)\{ -webkit-tap-highlight-color: transparent; \}/)[1];
  const selectors = rule.trim().split(', ');
  for (const selector of ['button', 'a', 'label', 'summary', 'input[type="checkbox"]', 'input[type="radio"]', '[role="button"]', '[role="option"]', '[role="menuitem"]', '[role="switch"]']) {
    assert.ok(selectors.includes(selector), selector);
  }
  assert.ok(!selectors.includes('select'));
  assert.ok(!selectors.includes('input'));
  assert.doesNotMatch(css, /user-select|outline/);
  runtime.dispose();
});

test('declarations require a mount function and explicit style URLs', () => {
  assert.throws(() => validatePluginView({}), /mount/);
  assert.throws(() => validatePluginView({ mount() {}, styles: [null] }), /资源地址/);
  assert.throws(() => validatePluginView({ mount() {}, styles: 'style.css' }), /资源地址/);
  validatePluginView({ mount() {}, styles: [new URL('https://example.test/style.css')] });
});

test('mount waits for declared styles and deduplicates URLs', async () => {
  const { root, start } = fixture();
  let mounted = false;
  const runtime = start({ styles: ['style.css', 'style.css'], mount() { mounted = true; } });
  assert.equal(mounted, false);
  assert.equal(root.children.length, 2);
  root.children[1].dispatchEvent(new Event('load'));
  await runtime.ready;
  assert.equal(mounted, true);
  runtime.dispose();
  assert.equal(root.children.length, 0);
});

test('closing during style loading cancels initialization', async () => {
  const { root, parent, start } = fixture();
  let calls = 0;
  const runtime = start({ styles: ['style.css'], mount() { calls++; } });
  parent.dispose();
  await assert.rejects(runtime.ready, { name: 'AbortError' });
  assert.equal(calls, 0);
  assert.equal(root.children.length, 0);
});

test('style failure prevents business mounting and releases content', async () => {
  const { root, start } = fixture();
  const runtime = start({ styles: ['style.css'], mount() { assert.fail('must not mount'); } });
  root.children[1].dispatchEvent(new Event('error'));
  await assert.rejects(runtime.ready, /样式加载失败/);
  assert.equal(root.children.length, 0);
});

test('late async mounting cleanup runs once after the parent closes', async () => {
  const { parent, root, start } = fixture();
  let finish;
  let disposed = 0;
  const runtime = start({ mount: () => new Promise(resolve => { finish = resolve; }) });
  await settle();
  parent.dispose();
  root.append(new Element());
  finish(() => disposed++);
  await runtime.ready;
  runtime.dispose();
  assert.equal(disposed, 1);
  assert.equal(root.children.length, 0);
});

test('mount failure runs registered cleanup and isolates cleanup errors', async () => {
  const { root, errors, start } = fixture();
  let disposed = 0;
  const runtime = start({ mount(_root, scope) {
    scope.onDispose(() => disposed++);
    scope.onDispose(() => { throw new Error('清理失败'); });
    throw new Error('初始化失败');
  } });
  await assert.rejects(runtime.ready, /初始化失败/);
  assert.equal(disposed, 1);
  assert.equal(errors.length, 1);
  assert.equal(root.children.length, 0);
});

test('already closed parent never starts mounting', async () => {
  const { parent, root, start } = fixture();
  parent.dispose();
  const runtime = start({ mount() { assert.fail('must not mount'); } });
  await runtime.ready;
  assert.equal(root.children.length, 0);
});

test('page and modal content receive optional UI controls with their own lifetime', async () => {
  const { parent, start } = fixture();
  let scope;
  const runtime = start({ mount(_root, context) { scope = context; } });
  await runtime.ready;
  assert.equal(typeof scope.ui.enhanceSelect, 'function');
  assert.equal(scope.signal.aborted, false);
  parent.dispose();
  assert.equal(scope.signal.aborted, true);
});
