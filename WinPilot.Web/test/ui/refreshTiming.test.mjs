import assert from 'node:assert/strict';
import test from 'node:test';
import { formatRefreshTime, isCustomRefreshIntervalValid, refreshIntervalMilliseconds } from '../../src/ui/refreshTiming.ts';

test('refresh interval validation retains integer bounds and rejects empty numeric input', () => {
  for (const value of [1, 15, 600]) assert.equal(isCustomRefreshIntervalValid(value), true);
  for (const value of [0, -1, 601, 1.5, NaN, Infinity]) {
    assert.equal(isCustomRefreshIntervalValid(value), false);
  }
});

test('automatic refresh only schedules enabled valid intervals', () => {
  for (const interval of ['1', '3', '5', '10']) {
    assert.equal(refreshIntervalMilliseconds(true, interval, NaN), Number(interval) * 1000);
    assert.equal(refreshIntervalMilliseconds(false, interval, 15), 0);
  }
  assert.equal(refreshIntervalMilliseconds(true, 'custom', 600), 600000);
  assert.equal(refreshIntervalMilliseconds(false, 'custom', 15), 0);
  for (const value of [0, 601, 1.5, NaN]) {
    assert.equal(refreshIntervalMilliseconds(true, 'custom', value), 0);
  }
});

test('refresh time preserves rounding, future timestamps and the one hour cap', () => {
  const capturedAt = new Date(100000);
  assert.equal(formatRefreshTime(undefined, 100000), '尚未刷新');
  for (const [elapsed, expected] of [
    [-1000, '刚刚'], [0, '刚刚'], [999, '刚刚'], [1000, '1 秒前'],
    [59999, '59 秒前'], [60000, '1 分钟前'], [3599999, '59 分钟前'],
    [3600000, '1 小时前'], [7200000, '1 小时前'],
  ]) assert.equal(formatRefreshTime(capturedAt, capturedAt.getTime() + elapsed), expected);
});
