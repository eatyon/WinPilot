import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';
import { apiErrorMessage } from '../support/apiErrors.mjs';

const exports = {};
runInNewContext(loadScriptModule(new URL('../../src/features/plugins/page/PluginPageResponse.ts', import.meta.url)), {
  exports, require: () => ({ apiErrorMessage }), DOMException, Error,
});
const { PluginPageSessionExpiredError, readPluginResponse } = exports;

test('valid business data is returned without interpreting business success', async () => {
  assert.deepEqual(await readPluginResponse(Response.json({ succeeded: false, message: '业务失败' })), { succeeded: false, message: '业务失败' });
});

test('invalid JSON in a successful response is reported explicitly', async () => {
  await assert.rejects(readPluginResponse(new Response('invalid')), /有效 JSON/);
});

test('HTTP errors preserve server detail or report the status', async () => {
  await assert.rejects(readPluginResponse(Response.json({ detail: '操作被禁止' }, { status: 400 })), /操作被禁止/);
  await assert.rejects(readPluginResponse(Response.json({ message: '安全模式限制' }, { status: 403 })), /安全模式限制/);
  await assert.rejects(readPluginResponse(Response.json({ errors: { field: ['配置不能为空'] } }, { status: 400 })), /配置不能为空/);
  await assert.rejects(readPluginResponse(Response.json(null, { status: 500 })), /HTTP 500/);
  await assert.rejects(readPluginResponse(new Response('server error', { status: 500 })), /HTTP 500/);
  await assert.rejects(readPluginResponse(Response.json({}, { status: 403 })), /HTTP 403/);
});

test('missing sessions produce a dedicated expiry error', async () => {
  await assert.rejects(readPluginResponse(new Response('', { status: 404 })), PluginPageSessionExpiredError);
});

test('cancelled response reading remains a cancellation rather than a JSON error', async () => {
  const response = { status: 200, ok: true, json: async () => { throw new DOMException('cancelled', 'AbortError'); } };
  await assert.rejects(readPluginResponse(response), { name: 'AbortError' });
});
