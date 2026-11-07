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

test('notification variables and descriptions share the library information icon', () => {
  const action = readFileSync(new URL('../../src/features/tasks/TaskActionEditor.vue', import.meta.url), 'utf8');
  assert.match(action, /aria-label="查看可用变量"[\s\S]*?<AppIcon name="info" \/>/);
  assert.doesNotMatch(action, /<AppIcon name="help"/);
  const help = readFileSync(new URL('../../src/components/HelpHint.vue', import.meta.url), 'utf8');
  assert.match(help, /<AppIcon name="info"/);
  assert.doesNotMatch(help, /<AppIcon name="help"/);
});

test('host help, action and disclosure controls use the common icon component instead of glyphs', () => {
  const files = ['components/HelpHint.vue', 'features/tasks/TasksView.vue', 'features/plugins/PluginSettingField.vue', 'components/KeyboardKeyPicker.vue', 'features/files/FilesView.vue', 'features/auth/LoginView.vue'];
  for (const file of files) {
    const source = readFileSync(new URL(`../../src/${file}`, import.meta.url), 'utf8');
    if (file === 'features/plugins/PluginSettingField.vue') assert.match(source, /<NCollapseItem/);
    else assert.match(source, /import AppIcon from/);
    assert.doesNotMatch(source, /<svg|<path|>\?<|▸|▾|＋|×/);
  }
  const helper = readFileSync(new URL('../../src/components/HelpHint.vue', import.meta.url), 'utf8');
  assert.equal([...helper.matchAll(/<AppIcon name="info"/g)].length, 3);
});

test('description icon opacity is local and custom icon leftovers are removed', () => {
  const icon = readFileSync(new URL('../../src/components/AppIcon.vue', import.meta.url), 'utf8');
  const help = readFileSync(new URL('../../src/components/HelpHint.vue', import.meta.url), 'utf8');
  assert.doesNotMatch(icon, /InfoPlain|info-plain|createLucideIcon|CircleHelp/);
  assert.equal([...help.matchAll(/name="info" class="help-hint-icon"/g)].length, 3);
  assert.match(help, /\.n-button \.help-hint-icon \{ width: 14px; height: 14px; opacity: 0\.7; \}/);
  const action = readFileSync(new URL('../../src/features/tasks/TaskActionEditor.vue', import.meta.url), 'utf8');
  assert.doesNotMatch(action, /help-hint-icon/);
});
