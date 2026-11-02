import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';

test('loading bar preference hides the bar without unmounting page content', () => {
  const source = readFileSync(new URL('../../src/components/AppTheme.vue', import.meta.url), 'utf8');
  assert.match(source, /<NLoadingBarProvider :container-style="loadingBarEnabled \? undefined : \{ display: 'none' \}">/);
  assert.doesNotMatch(source, /<NLoadingBarProvider[^>]*v-if/);
});

test('loading bar follows navigation and immediately responds to its independent switch', () => {
  const status = { value: 'loading' }, enabled = { value: true };
  const calls = [];
  let update;
  runInNewContext(loadScriptModule(new URL('../../src/components/AppRouteLoading.vue', import.meta.url)), {
    exports: {},
    require: name => {
      if (name === 'vue') return { watch(sources, callback, options) {
        assert.equal(sources[0], status);
        assert.equal(sources[1], enabled);
        assert.equal(options.flush, 'sync');
        update = () => callback(sources.map(source => source.value));
        if (options.immediate) update();
      } };
      if (name === 'naive-ui') return { useLoadingBar: () => Object.fromEntries(
        ['start', 'finish', 'error'].map(method => [method, () => calls.push(method)])) };
      if (name === '../ui/navigationProgress') return { navigationStatus: status };
      if (name === '../ui/panelEffects') return { usePanelEffects: () => ({ loadingBarEnabled: enabled }) };
      throw new Error(`Unexpected import: ${name}`);
    },
  });
  enabled.value = false;
  update();
  status.value = 'error';
  update();
  status.value = 'loading';
  enabled.value = true;
  update();
  status.value = 'error';
  update();
  status.value = 'idle';
  update();
  assert.deepEqual(calls, ['start', 'finish', 'finish', 'start', 'error', 'finish']);
});
