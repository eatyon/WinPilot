import assert from 'node:assert/strict';
import test from 'node:test';
import { combinePageSignal, createPluginPageLifetime } from '../../src/features/plugins/page/PluginPageLifetime.ts';

test('page cleanup runs once, can unregister, and continues after failure', () => {
  const errors = [];
  const lifetime = createPluginPageLifetime(error => errors.push(error));
  const calls = [];
  lifetime.onDispose(() => calls.push('一'));
  const unregister = lifetime.onDispose(() => calls.push('取消'));
  unregister();
  lifetime.onDispose(() => { throw new Error('清理失败'); });
  lifetime.onDispose(() => calls.push('二'));
  lifetime.dispose();
  lifetime.dispose();
  lifetime.onDispose(() => calls.push('迟到挂载'));
  assert.deepEqual(calls, ['二', '一', '迟到挂载']);
  assert.equal(errors.length, 1);
  assert.equal(lifetime.signal.aborted, true);
});

test('custom request cancellation does not bypass page cancellation', () => {
  for (const source of ['page', 'request']) {
    const page = new AbortController();
    const request = new AbortController();
    const combined = combinePageSignal(page.signal, request.signal);
    (source === 'page' ? page : request).abort();
    assert.equal(combined.signal.aborted, true);
    combined.dispose();
  }
});

test('combined signal respects existing cancellation and removes listeners after completion', () => {
  const page = new AbortController();
  const request = new AbortController();
  const completed = combinePageSignal(page.signal, request.signal);
  completed.dispose();
  page.abort();
  assert.equal(completed.signal.aborted, false);
  const cancelled = combinePageSignal(page.signal, request.signal);
  assert.equal(cancelled.signal.aborted, true);
  cancelled.dispose();
});
