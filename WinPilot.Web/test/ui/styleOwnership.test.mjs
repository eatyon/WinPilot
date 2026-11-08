import { readStylesheet } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import test from 'node:test';
import postcss from 'postcss';

const read = path => readStylesheet(new URL(`../../src/${path}`, import.meta.url));
function rules(source, selector) {
  const result = [];
  postcss.parse(source).walkRules(rule => { if (rule.selector === selector) result.push(rule); });
  return result;
}
const declarations = rule => Object.fromEntries(rule.nodes.filter(node => node.type === 'decl').map(node => [node.prop, node.value]));

test('modal layout belongs to its component while shared scrollbar sizing stays global', () => {
  const styles = read('styles/features.css');
  assert.equal(rules(styles, '.app-modal').length, 0);
  assert.equal(rules(styles, '.modal-content').length, 0);
  const modal = readStylesheet(new URL('../../src/components/AppModal.vue', import.meta.url));
  assert.deepEqual(declarations(rules(modal, '.app-modal')[0]), {
    display: 'flex', 'flex-direction': 'column',
    width: 'min(var(--dialog-width, 560px), calc(100vw - 32px))',
    'max-height': 'calc(100svh - 48px)',
  });
  assert.deepEqual(declarations(rules(modal, '.modal-content')[0]), { 'min-height': '0' });
  const shared = read('styles/components.css');
  assert.equal(rules(shared, '.app-modal').length, 0);
  assert.equal(rules(shared, '.modal-content').length, 0);
  assert.deepEqual(declarations(rules(shared, '.app-modal > .n-card__content-scrollbar, .app-detail-dialog > .n-card__content-scrollbar')[0]), {
    'min-height': '0', height: 'auto', flex: '1',
  });
});

test('page padding is owned by layout instead of an overridden legacy breakpoint', () => {
  assert.equal(rules(read('styles/features.css'), '.page-content').length, 0);
  assert.equal(rules(read('styles/features.css'), '.app-shell').length, 0);
  assert.equal(declarations(rules(read('styles/layout.css'), '.page-content')[0]).padding, 'clamp(16px, 3vw, 24px)');
});

test('plugin cards use a single grid layout', () => {
  const styles = readStylesheet(new URL('../../src/features/plugins/PluginsView.vue', import.meta.url));
  const global = rules(styles, '.plugin-card').filter(rule => rule.parent.type === 'root').map(declarations);
  assert.equal(global.length, 1);
  assert.deepEqual(global.flatMap(rule => rule.display ? [rule.display] : []), ['grid']);
  assert.ok(global.some(rule => rule['align-items'] === 'center' && rule['justify-content'] === 'space-between'));
});

test('plugin card and detail headers keep their mobile alignment after their desktop defaults', () => {
  for (const [file, selector] of [
    ['PluginsView.vue', '.plugin-card'],
    ['PluginInfoView.vue', '.plugin-detail-hero'],
  ]) {
    const styles = readStylesheet(new URL(`../../src/features/plugins/${file}`, import.meta.url));
    const matches = rules(styles, selector);
    const desktop = matches.find(rule => rule.parent.type === 'root');
    const mobile = matches.find(rule => rule.parent.type === 'atrule' && rule.parent.params === '(max-width: 640px)');
    assert.equal(declarations(desktop)['align-items'], 'center');
    assert.equal(declarations(mobile)['align-items'], 'stretch');
    if (file === 'PluginsView.vue') {
      const narrow = matches.find(rule => rule.parent.type === 'atrule' && rule.parent.params === '(max-width: 767px)');
      assert.equal(declarations(narrow)['grid-template-columns'], 'minmax(0, 1fr)');
      assert.ok(narrow.source.start.offset > desktop.source.start.offset);
    } else {
      assert.equal(declarations(mobile)['flex-direction'], 'column');
    }
    assert.ok(mobile.source.start.offset > desktop.source.start.offset);
  }
});

test('refresh timestamps wrap on narrow screens', () => {
  assert.equal(rules(read('styles/features.css'), '.capture-time').length, 0);
  assert.equal(rules(read('styles/features.css'), '.capture-time strong').length, 0);
  const styles = readStylesheet(new URL('../../src/components/RefreshToolbar.vue', import.meta.url));
  const times = rules(styles, '.capture-time');
  assert.equal(declarations(times[0])['white-space'], 'nowrap');
  assert.equal(times[1].parent.params, '(max-width: 767px)');
  assert.deepEqual(declarations(times[1]), { 'white-space': 'normal' });
});
