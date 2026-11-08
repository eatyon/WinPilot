import { loadComponentModule } from '../support/scriptModule.mjs';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import * as vue from 'vue';
import { renderToString } from 'vue/server-renderer';
import { NSpin } from 'naive-ui';

test('shared loading keeps the existing spinner, labels and accessible state without unused skeleton modes', async () => {
  const source = readFileSync(new URL('../../src/components/AppLoading.vue', import.meta.url), 'utf8');
  const context = { exports: {}, require: name => name === 'vue' ? vue : { NSpin } };
  runInNewContext(loadComponentModule(new URL('../../src/components/AppLoading.vue', import.meta.url)), context);
  const html = await renderToString(vue.createSSRApp(context.exports.default, { label: '正在读取目录' }));
  assert.match(html, /role="status"/);
  assert.match(html, /aria-label="正在读取目录"/);
  assert.match(html, /aria-busy="true"/);
  assert.doesNotMatch(html, /class="n-skeleton/);
  assert.match(html, /class="n-spin/);
  const fallback = await renderToString(vue.createSSRApp(context.exports.default));
  assert.match(fallback, /aria-label="正在加载"/);
  assert.match(fallback, /class="app-loading app-loading--loading"/);
  assert.match(source, /min-height: 96px; place-items: center;/);
  assert.match(source, /gap: 12px; margin-block: 12px;/);
  assert.doesNotMatch(source, /NSkeleton|variant|rows|actionWidth|actionHeight|showDescription|app-loading--list|app-loading--table/);
});
