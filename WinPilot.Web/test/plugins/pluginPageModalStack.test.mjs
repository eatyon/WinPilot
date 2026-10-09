import assert from 'node:assert/strict';
import test from 'node:test';
import { createPluginModalStack } from '../../src/features/plugins/page/PluginPageModalStack.ts';

function fixture() {
  const errors = [];
  let updates = 0;
  const stack = createPluginModalStack(() => updates++, error => errors.push(error));
  return { stack, errors, updates: () => updates };
}

test('confirmation can nest inside an editor without closing it', async () => {
  const { stack } = fixture();
  const editor = stack.openModal({ title: '编辑', mount() {} });
  const accepted = stack.confirm({ message: '确认保存？' });
  assert.equal(stack.entries.length, 2);
  stack.close(stack.entries.at(-1), true);
  assert.equal(await accepted, true);
  assert.equal(stack.entries.length, 1);
  assert.equal(editor.signal.aborted, false);
  editor.close();
  await editor.closed;
});

test('closing a parent cancels descendants and disposes content once in reverse order', async () => {
  const { stack } = fixture();
  const disposed = [];
  const parent = stack.openModal({ title: '父', mount() {} });
  parent.signal.addEventListener('abort', () => disposed.push('父'));
  const child = stack.openModal({ title: '子', mount() {} });
  child.signal.addEventListener('abort', () => disposed.push('子'));
  const confirmation = stack.confirm({ message: '确认？' });
  parent.close();
  parent.close();
  await Promise.all([parent.closed, child.closed]);
  assert.equal(await confirmation, false);
  assert.equal(stack.entries.length, 0);
  assert.deepEqual(disposed, ['子', '父']);
  assert.ok(parent.signal.aborted && child.signal.aborted);
});

test('a stale handle cannot change or close a new modal', () => {
  const { stack } = fixture();
  const stale = stack.openModal({ title: '旧', mount() {} });
  stale.close();
  stack.openModal({ title: '新', mount() {} });
  stale.update({ title: '错误' });
  stale.close();
  assert.equal(stack.entries[0].options.title, '新');
});

test('parent options can update while a child is open', () => {
  const { stack } = fixture();
  const parent = stack.openModal({ title: '父', mount() {} });
  stack.openModal({ title: '子', mount() {} });
  parent.update({ title: '已更新', size: 'large', dismissible: false });
  assert.equal(stack.entries[0].options.title, '已更新');
  assert.equal(stack.entries[0].options.size, 'large');
  stack.dismiss(stack.entries[0]);
  assert.equal(stack.entries.length, 2);
});

test('invalid modal declarations are rejected before entering the stack', () => {
  const { stack } = fixture();
  assert.throws(() => stack.openModal({ title: '错误' }), /mount/);
  assert.equal(stack.entries.length, 0);
});

test('asynchronous actions prevent duplicate execution and dismiss while pending', async () => {
  const { stack } = fixture();
  let finish;
  let calls = 0;
  const pending = new Promise(resolve => { finish = resolve; });
  stack.openModal({ title: '保存', mount() {}, actions: [{ label: '保存', onClick: () => { calls++; return pending; } }] });
  const entry = stack.entries[0];
  const running = stack.runAction(entry, 0);
  await stack.runAction(entry, 0);
  stack.dismiss(entry);
  assert.equal(calls, 1);
  assert.equal(stack.entries.length, 1);
  finish();
  await running;
  stack.dismiss(entry);
  assert.equal(stack.entries.length, 0);
});

test('an action can await a nested confirmation', async () => {
  const { stack } = fixture();
  let accepted;
  stack.openModal({ title: '编辑', mount() {}, actions: [{ label: '保存', onClick: async () => { accepted = await stack.confirm({ message: '保存？' }); } }] });
  const running = stack.runAction(stack.entries[0], 0);
  stack.close(stack.entries.at(-1), true);
  await running;
  assert.equal(accepted, true);
  assert.equal(stack.entries[0].pending, false);
});

test('action errors are reported once and do not close the editor', async () => {
  const { stack, errors } = fixture();
  stack.openModal({ title: '编辑', mount() {}, actions: [{ label: '保存', onClick: async () => { throw new Error('失败'); } }] });
  await stack.runAction(stack.entries[0], 0);
  assert.equal(errors.length, 1);
  assert.equal(stack.entries.length, 1);
  assert.equal(stack.entries[0].pending, false);
});

test('disabled actions do not execute and late errors after close are ignored', async () => {
  const { stack, errors } = fixture();
  let fail;
  let calls = 0;
  const handle = stack.openModal({ title: '编辑', mount() {}, actions: [{ label: '保存', disabled: true, onClick: () => { calls++; } }] });
  await stack.runAction(stack.entries[0], 0);
  assert.equal(calls, 0);
  handle.update({ actions: [{ label: '保存', onClick: () => new Promise((_, reject) => { fail = reject; }) }] });
  const running = stack.runAction(stack.entries[0], 0);
  handle.close();
  fail(new Error('迟到错误'));
  await running;
  assert.equal(errors.length, 0);
});

test('dispose releases all dialogs and prevents reopening', async () => {
  const { stack, errors } = fixture();
  const handle = stack.openModal({ title: '编辑', mount() {} });
  const confirmation = stack.confirm({ message: '确认？' });
  stack.dispose();
  stack.dispose();
  await handle.closed;
  assert.equal(await confirmation, false);
  assert.equal(errors.length, 0);
  assert.equal(await stack.confirm({ message: '确认？' }), false);
  assert.throws(() => stack.openModal({ title: '编辑', mount() {} }), { name: 'AbortError' });
});
