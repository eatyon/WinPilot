import { readStylesheet } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { elements, attribute, binding } from '../support/template.mjs';

test('refresh buttons retain visible labels and use the library loading layout', () => {
  const files = ['components/RefreshToolbar.vue', 'features/files/FilesView.vue',
    'features/programs/ProgramsView.vue', 'features/logs/LogsView.vue', 'features/desktop/DesktopView.vue',
    'features/plugins/PluginsView.vue'];
  let count = 0;
  for (const file of files) {
    const source = readFileSync(new URL(`../../src/${file}`, import.meta.url), 'utf8');
    const buttons = elements(source, node => node.tag === 'NButton' && /^(刷新|重新扫描)$/.test(attribute(node, 'aria-label') ?? ''));
    for (const node of buttons) {
      count++;
      for (const name of ['loading', 'disabled', 'aria-busy']) {
        assert.ok(binding(node, name), `${file}: ${name}`);
      }
      assert.ok(binding(node, 'disabled').includes(`!${binding(node, 'loading')}`), `${file}: execution loading must not also disable the button`);
      if (file === 'features/plugins/PluginsView.vue')
        assert.ok(binding(node, 'disabled').includes('changingIds.size > 0'));
      assert.ok(node.children.some(node => node.type === 2 && /刷新|重新扫描/.test(node.content)));
      assert.ok(!node.children.some(node => node.type === 5));
    }
  }
  assert.ok(count > 0, 'refresh button scan must find controls');
  const styles = readStylesheet(new URL('../../src/styles/components.css', import.meta.url));
  assert.match(styles, /\.n-button\[aria-busy="true"\]:disabled:not\(\.n-button--loading\)\s*\{\s*cursor:\s*pointer;\s*\}/);
});
