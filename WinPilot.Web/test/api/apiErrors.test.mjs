import assert from 'node:assert/strict';
import test from 'node:test';
import { apiErrorMessage, readApiError, userErrorMessage, WinPilotConnectionError } from '../support/apiErrors.mjs';

test('empty error messages fall back while connection errors remain suppressed', () => {
  assert.equal(userErrorMessage(new Error(), '操作失败'), '操作失败');
  assert.equal(userErrorMessage(new Error('  '), '操作失败'), '操作失败');
  assert.equal(userErrorMessage(new Error('具体原因'), '操作失败'), '具体原因');
  assert.equal(userErrorMessage(new WinPilotConnectionError(), '操作失败'), '');
});

test('API error messages support existing formats with consistent precedence', () => {
  assert.equal(apiErrorMessage({ detail: '  具体原因  ', errors: { field: ['校验错误'] }, message: '提示', title: '标题' }, '失败'), '具体原因');
  assert.equal(apiErrorMessage({ detail: '', errors: { first: [], second: ['', '字段不能为空'] }, title: '校验失败' }, '失败'), '字段不能为空');
  assert.equal(apiErrorMessage({ message: '安全模式下不可执行此操作' }, '失败'), '安全模式下不可执行此操作');
  assert.equal(apiErrorMessage({ title: 'API 不存在' }, '失败'), 'API 不存在');
});

test('invalid or empty API errors use the page fallback without throwing', () => {
  for (const body of [null, undefined, [], 'error', 1, {}, { message: {} }, { detail: ' ', errors: { field: [null, 1] }, title: false }])
    assert.equal(apiErrorMessage(body, '页面提示'), '页面提示');
});

test('error body parsing preserves cancellation and supports page-owned readers', async () => {
  assert.equal(await readApiError({ json: async () => { throw new SyntaxError('HTML'); } }, '页面提示'), '页面提示');
  const cancellation = new DOMException('cancelled', 'AbortError');
  await assert.rejects(readApiError({ json: async () => { throw cancellation; } }, '页面提示'), error => error === cancellation);
  const response = {};
  assert.equal(await readApiError(response, '页面提示', async value => {
    assert.equal(value, response);
    return { message: '服务提示' };
  }), '服务提示');
});
