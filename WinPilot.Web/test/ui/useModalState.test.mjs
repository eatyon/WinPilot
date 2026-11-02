import assert from 'node:assert/strict';
import test from 'node:test';
import { useModalState } from '../../src/ui/useModalState.ts';

test('modal retains its content until the library finishes leaving', () => {
  let cleanups = 0;
  const modal = useModalState(() => undefined, Boolean, () => { cleanups++; });
  modal.open('详情');
  assert.equal(modal.show, true);
  modal.close();
  assert.equal(modal.show, false);
  assert.equal(modal.data.value, '详情');
  assert.equal(cleanups, 0);
  assert.equal(modal.afterLeave(), true);
  assert.equal(modal.data.value, undefined);
  assert.equal(cleanups, 1);
  assert.equal(modal.afterLeave(), false);
  assert.equal(cleanups, 1);
});

test('a stale leave callback does not erase a quickly reopened modal', () => {
  const modal = useModalState(() => undefined);
  modal.open('旧内容');
  modal.close();
  modal.open('新内容');
  assert.equal(modal.afterLeave(), false);
  assert.equal(modal.show, true);
  assert.equal(modal.data.value, '新内容');
});

test('reset immediately clears protected content and creates a fresh collection', () => {
  const modal = useModalState(() => [], value => value.length > 0);
  const initial = modal.data.value;
  modal.open(['文件']);
  modal.reset();
  assert.equal(modal.show, false);
  assert.deepEqual(modal.data.value, []);
  assert.notEqual(modal.data.value, initial);
  assert.equal(modal.afterLeave(), false);
});

test('zero is a valid selected index and is retained during leaving', () => {
  const modal = useModalState(() => -1, value => value >= 0);
  modal.open(0);
  assert.equal(modal.show, true);
  modal.close();
  assert.equal(modal.data.value, 0);
  modal.afterLeave();
  assert.equal(modal.data.value, -1);
});
