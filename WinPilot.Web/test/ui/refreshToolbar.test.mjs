import assert from 'node:assert/strict';
import * as delayedLoading from '../../src/ui/useDelayedLoading.ts';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';

test('refresh interval editing preserves invalid input for parent validation', () => {
  const emitted = [];
  const context = {
    exports: {}, require: name => name.endsWith('/useDelayedLoading') ? delayedLoading : name === 'vue' ? { useId: () => 'refresh-test' } : {}, defineProps: () => ({}),
    defineEmits: () => (...args) => emitted.push(args),
  };
  runInNewContext(loadScriptModule(new URL('../../src/components/RefreshToolbar.vue', import.meta.url), 'updateSeconds'), context);
  for (const value of ['1', '600', '601', '1.5', '0', '']) context.exports.updateSeconds(value);
  assert.deepEqual(emitted.map(([event]) => event), Array(6).fill('update:customSeconds'));
  assert.deepEqual(emitted.map(([, value]) => value), [1, 600, 601, 1.5, 0, Number.NaN]);
});

test('both pages use shared controls while retaining their own refresh operation', () => {
  for (const [file, action, enabled] of [
    ['home/HomeView.vue', 'refreshStatus', 'autoEnabled'],
    ['desktop/DesktopView.vue', 'captureScreenshot', 'autoRefreshEnabled'],
  ]) {
    const source = readFileSync(new URL('../../src/features/' + file, import.meta.url), 'utf8');
    assert.match(source, /<RefreshToolbar/);
    assert.ok(source.includes(`v-model:enabled="${enabled}"`));
    assert.ok(source.includes(`@refresh="${action}"`));
    assert.ok(source.includes(':valid="isCustomIntervalValid"'));
  }
  const toolbar = readFileSync(new URL('../../src/components/RefreshToolbar.vue', import.meta.url), 'utf8');
  assert.match(toolbar, /<NButton[^>]*:loading="showLoading"[^>]*:disabled="refreshing && !showLoading"/);
  assert.doesNotMatch(toolbar, /setInterval|fetch\(|localStorage|apiFetch/);
});

test('custom refresh interval keeps error text after the input without form feedback below it', () => {
  const toolbar = readFileSync(new URL('../../src/components/RefreshToolbar.vue', import.meta.url), 'utf8');
  assert.match(toolbar, /<NFormItem\s+v-if="interval === 'custom'"[\s\S]*?:show-feedback="false"/);
  assert.match(toolbar, /<\/NFormItem>\s*<NText v-if="interval === 'custom' && !valid" type="error" role="alert">\s*请输入 1 至 600 的整数秒数/);
  assert.doesNotMatch(toolbar, /<template #feedback>/);
  assert.match(toolbar, /:aria-invalid="!valid"/);
  assert.doesNotMatch(toolbar, /NCollapseTransition|ResizeObserver|useAutoHeightTransition|<Transition|refresh-toolbar-frame|refresh-validation/);
});
