import { readStylesheet } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, readdirSync } from 'node:fs';
import { parse } from '@vue/compiler-sfc';
import { elements } from '../support/template.mjs';
import { runInNewContext } from 'node:vm';
import postcss from 'postcss';
import { textPopoverStyle } from '../../src/ui/textPopoverStyle.ts';

test('hover hints delegate appearance and lifecycle to the library popover', () => {
  const source = readFileSync(new URL('../../src/components/AppTooltip.vue', import.meta.url), 'utf8');
  assert.match(source, /import \{ NPopover \} from 'naive-ui'/);
  assert.match(source, /<NPopover trigger="hover" :disabled="!text \|\| !canHover" :show="shown" @update:show="updateShow"/);
  assert.match(source, /:style="textPopoverStyle"/);
  assert.deepEqual(textPopoverStyle, { maxWidth: 'min(320px, calc(100vw - 32px))', whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' });
  assert.match(source, /<span v-if="disabledTrigger" class="app-tooltip-trigger"><slot \/><\/span>/);
  assert.match(source, /<slot v-else \/>/);
  assert.doesNotMatch(source, /setTimeout|onMounted|backgroundColor|zIndex/);
  const help = readFileSync(new URL('../../src/components/HelpHint.vue', import.meta.url), 'utf8');
  assert.match(help, /<NPopover v-else trigger="click"/);
});

test('hover capability follows the input device, clears sticky hints and releases its listener', () => {
  const source = readFileSync(new URL('../../src/components/AppTooltip.vue', import.meta.url), 'utf8');
  const code = source.slice(source.indexOf('const hoverMedia'), source.indexOf('</script>'));
  let listener;
  let cleanup;
  const media = { matches: false, addEventListener: (type, fn) => { assert.equal(type, 'change'); listener = fn; }, removeEventListener: (type, fn) => { assert.equal(type, 'change'); assert.equal(fn, listener); listener = undefined; } };
  const context = { window: { matchMedia: query => { assert.equal(query, '(hover: hover) and (pointer: fine)'); return media; } }, ref: value => ({ value }), onScopeDispose: fn => { cleanup = fn; } };
  runInNewContext(code.replace('value: boolean', 'value') + '\nthis.state = { canHover, shown };', context);
  context.updateShow(true);
  assert.equal(context.state.shown.value, false);
  media.matches = true;
  listener();
  context.updateShow(true);
  assert.equal(context.state.shown.value, true);
  media.matches = false;
  listener();
  assert.equal(context.state.shown.value, false);
  media.matches = true;
  listener();
  assert.equal(context.state.shown.value, false);
  cleanup();
  assert.equal(listener, undefined);
});

test('touch button reset excludes active, pressed, disabled and keyboard focus states', () => {
  const css = readStylesheet(new URL('../../src/styles/components.css', import.meta.url));
  assert.match(css, /@media \(hover: none\), \(pointer: coarse\)/);
  const rules = [];
  postcss.parse(css).walkRules(rule => {
    if (rule.selector.startsWith('.n-button:') && rule.selector.includes(':is(:hover, :focus)')) rules.push(rule);
  });
  assert.equal(rules.length, 2);
  for (const rule of rules) {
    for (const state of [':disabled', '.n-button--pressed', ':active', ':focus-visible']) assert.ok(rule.selector.includes(`:not(${state})`));
    assert.equal(rule.parent.params, '(hover: none), (pointer: coarse)');
  }
  assert.match(css, /\.n-base-close:not\(\.n-base-close--disabled\):not\(:active\):not\(:focus-visible\):is\(:hover, :focus\)::before\s*\{\s*background-color: transparent;/);
});

test('custom hover rules require a fine hover pointer while selected and focus rules stay independent', () => {
  for (const file of ['style.css', 'features/files/FilesView.vue', 'features/settings/tabs/AboutSettings.vue', 'styles/scrollbars.css']) {
    const source = readFileSync(new URL(`../../src/${file}`, import.meta.url), 'utf8');
    const css = file.endsWith('.vue') ? parse(source).descriptor.styles.map(style => style.content).join('\n') : source;
    postcss.parse(css).walkRules(rule => {
      if (!rule.selector.includes(':hover')) return;
      assert.equal(rule.parent.params, '(hover: hover) and (pointer: fine)', file);
      assert.ok(rule.selectors.every(selector => selector.includes(':hover')), file);
    });
  }
});

test('host hover hints no longer use native titles while dialog and card titles remain', () => {
  const titles = new Set(['AppModal', 'NModal', 'NCard', 'NDrawerContent', 'NThing', 'KeyboardKeyPicker']);
  let tooltipCount = 0;
  let headingCount = 0;
  function check(directory) {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const file = new URL(entry.name, directory);
      if (entry.isDirectory()) { check(new URL(`${entry.name}/`, directory)); continue; }
      if (!entry.name.endsWith('.vue')) continue;
      for (const node of elements(readFileSync(file, 'utf8'), () => true)) {
        if (node.tag === 'AppTooltip') tooltipCount++;
        for (const prop of node.props) {
          if ((prop.type === 6 && prop.name === 'title') || (prop.type === 7 && prop.name === 'bind' && prop.arg?.content === 'title')) {
            if (prop.type === 6 && !prop.value?.content) continue;
            assert.ok(titles.has(node.tag), `${file.pathname}: native title on ${node.tag}`);
            headingCount++;
          }
        }
      }
    }
  }
  check(new URL('../../src/', import.meta.url));
  assert.ok(tooltipCount > 0);
  assert.ok(headingCount >= 20);
});

test('redundant mobile hints and normal plugin switch hints stay removed', () => {
  const app = readFileSync(new URL('../../src/App.vue', import.meta.url), 'utf8');
  const nav = readFileSync(new URL('../../src/components/AppNavigation.vue', import.meta.url), 'utf8');
  assert.doesNotMatch(app, /<AppTooltip[^>]*:text="pageTitle"/);
  assert.doesNotMatch(nav, /AppTooltip/);
  assert.match(nav, /aria-label="打开导航菜单"/);
  for (const name of ['PluginHeader', 'PluginsView']) {
    const source = readFileSync(new URL(`../../src/features/plugins/${name}.vue`, import.meta.url), 'utf8');
    assert.match(source, /:text="!plugin.canChangeEnabled \? '无法启用' : undefined"/);
    assert.match(source, /:aria-label=/);
  }
});
