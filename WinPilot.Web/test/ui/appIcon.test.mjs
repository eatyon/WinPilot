import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import * as lucide from '@lucide/vue';
import { loadScriptModule } from '../support/scriptModule.mjs';

test('business icon names resolve to statically imported Lucide components', () => {
  const file = new URL('../../src/components/AppIcon.vue', import.meta.url);
  const context = { exports: {}, defineProps: () => ({}), require: () => lucide };
  runInNewContext(loadScriptModule(file, 'icons'), context);
  const names = [
    'menu', 'menu-expand', 'menu-collapse', 'palette', 'account', 'alert-circle', 'close',
    'rotate-cw', 'list', 'bot', 'home', 'desktop', 'apps', 'clock', 'schedule',
    'terminal', 'files', 'logs', 'plugins', 'settings', 'more', 'arrow', 'server',
    'shield', 'eye', 'eye-off', 'info', 'copy', 'lock', 'check-circle',
    'plus', 'chevron-down', 'chevron-right', 'chevron-up',
  ];
  assert.deepEqual(Object.keys(context.exports.icons).sort(), names.sort());
  for (const name of names) assert.ok(context.exports.icons[name], name);
  const source = readFileSync(file, 'utf8');
  assert.doesNotMatch(source, /import\s+\*|<path|<circle|<rect/);
  assert.match(source, /class="app-icon"/);
  assert.match(source, /<NIcon v-if="icons\[name\]" class="app-icon" aria-hidden="true">/);
  assert.match(source, /:stroke-width="1\.8" focusable="false"/);
});
