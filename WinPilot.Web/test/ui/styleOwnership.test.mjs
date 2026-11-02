import { readStylesheet } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { parse } from '@vue/compiler-sfc';
import postcss from 'postcss';

const read = path => path.endsWith('.css') ? readStylesheet(new URL(`../../src/${path}`, import.meta.url)) : readFileSync(new URL(`../../src/${path}`, import.meta.url), 'utf8');
function rules(source, selector) {
  const result = [];
  postcss.parse(source).walkRules(rule => { if (rule.selector === selector) result.push(rule); });
  return result;
}
const declarations = rule => Object.fromEntries(rule.nodes.filter(node => node.type === 'decl').map(node => [node.prop, node.value]));

test('style entry declares one ordered chain for base, pages, layout, components, transitions and scrollbars', () => {
  const entry = postcss.parse(readFileSync(new URL('../../src/style.css', import.meta.url), 'utf8'));
  assert.ok(entry.nodes.every(node => node.type === 'atrule' && node.name === 'import'));
  assert.deepEqual(entry.nodes.map(node => node.params), [
    "'./styles/base.css'", "'./styles/brand.css'", "'./styles/features.css'", "'./styles/markdown.css'",
    "'./styles/layout.css'", "'./styles/components.css'",
    "'./styles/transitions.css'", "'./styles/scrollbars.css'",
  ]);
  assert.doesNotMatch(read('styles/components.css'), /\.programs-view|\.plugin-module|\.desktop-frame|\.page-fade/);
  assert.doesNotMatch(read('styles/base.css'), /\.login-view|\.tasks-view|\.plugin-card/);
  const features = postcss.parse(readFileSync(new URL('../../src/styles/features.css', import.meta.url), 'utf8'));
  assert.ok(features.nodes.every(node => node.type === 'atrule' && node.name === 'import'));
  assert.deepEqual(features.nodes.map(node => node.params), [
    "'../features/settings/styles.css'", "'../features/tasks/styles.css'",
    "'../features/plugins/styles.css'", "'../features/security/styles.css'",
  ]);
});

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

test('business and component rules stay with their sole owner while shared rules stay global', () => {
  const shared = ['base', 'brand', 'features', 'layout', 'components'].map(name => read(`styles/${name}.css`)).join('\n');
  for (const [file, selector] of [
    ['features/auth/LoginView.vue', '.login-view'],
    ['features/home/HomeView.vue', '.system-status-view'],
    ['features/desktop/DesktopView.vue', '.desktop-frame'],
    ['features/programs/ProgramsView.vue', '.programs-view'],
    ['components/KeyboardKeyPicker.vue', '.virtual-keyboard'],
    ['components/AppIcon.vue', '.app-icon'],
    ['features/auth/LoginView.vue', '.login-brand-symbol'],
    ['App.vue', '.product-symbol'],
    ['components/AppNavigation.vue', '.app-menu-footer'],
    ['components/AppNavigation.vue', '.app-menu-divider'],
    ['features/terminal/TerminalView.vue', '.app-workspace:has(.terminal-view) > .n-scrollbar-container > .n-scrollbar-content'],
    ['features/terminal/TerminalView.vue', '.page-content:has(.terminal-view)'],
    ['features/terminal/TerminalView.vue', '.page-content:has(.terminal-view) > .route-page'],
    ['features/tasks/TasksView.vue', '.form-field.n-form-item > .form-field-content'],
    ['components/AppTooltip.vue', '.app-tooltip-trigger'],
    ['components/AppModal.vue', '.app-modal'],
    ['components/AppIcon.vue', '.n-icon.app-icon > svg'],
    ['features/logs/LogsView.vue', '.app-list-card'],
    ['features/programs/ProgramsView.vue', '.form-options'],
    ['features/desktop/DesktopView.vue', '.image-preview-actions'],
    ['features/settings/SettingsView.vue', '.settings-view'],
    ['features/settings/tabs/SecuritySettings.vue', '.security-password-form'],
    ['features/settings/tabs/AboutSettings.vue', '.about-link-row'],
    ['features/settings/tabs/GeneralSettings.vue', '.settings-port-control'],
    ['features/settings/tabs/NotificationSettings.vue', '.notification-channel-copy'],
    ['features/tasks/TasksView.vue', '.task-list'],
    ['features/tasks/CronEditor.vue', '.cron-editor'],
    ['features/tasks/TaskActionEditor.vue', '.task-action-editor'],
    ['features/tasks/TaskVariableHelp.vue', '.variable-help-content'],
    ['features/plugins/PluginsView.vue', '.plugin-card'],
    ['features/plugins/PluginInfoView.vue', '.plugin-info-list'],
    ['features/plugins/PluginHeader.vue', '.plugin-home-header'],
    ['features/plugins/PluginDetailView.vue', '.plugin-status-list'],
    ['features/plugins/PluginSettingField.vue', '.plugin-collection-heading'],
    ['features/plugins/PluginMarketView.vue', '.plugin-market-view'],
  ]) {
    assert.equal(rules(shared, selector).length, 0, selector);
    const styles = parse(read(file)).descriptor.styles.filter(style => !style.scoped && !style.src);
    assert.ok(styles.some(style => rules(style.content, selector).length > 0), `${file}: ${selector}`);
  }
  for (const selector of ['.settings-tab-content', '.task-validation-feedback', '.plugin-features-list', '.safe-mode-locked']) {
    assert.ok(rules(shared, selector).length > 0, selector);
  }
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
