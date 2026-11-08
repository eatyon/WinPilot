import { readStylesheet } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import test from 'node:test';
import { loadScriptModule } from '../support/scriptModule.mjs';
import { createAppShell } from '../support/appShell.mjs';
import { elements, attribute } from '../support/template.mjs';

test('sidebar preference defaults expanded, persists and tolerates unavailable storage', () => {
  let saved = null;
  let blocked = false;
  const context = {
    exports: {},
    localStorage: {
      getItem(key) { assert.equal(key, 'winpilot.sidebar-collapsed'); if (blocked) throw new Error('blocked'); return saved; },
      setItem(key, value) { assert.equal(key, 'winpilot.sidebar-collapsed'); if (blocked) throw new Error('blocked'); saved = value; },
    },
  };
  runInNewContext(loadScriptModule(new URL('../../src/ui/navigationLayout.ts', import.meta.url)), context);
  const { readSidebarCollapsed, saveSidebarCollapsed } = context.exports;
  assert.equal(readSidebarCollapsed(), false);
  saveSidebarCollapsed(true);
  assert.equal(readSidebarCollapsed(), true);
  saveSidebarCollapsed(false);
  assert.equal(readSidebarCollapsed(), false);
  saved = 'invalid';
  assert.equal(readSidebarCollapsed(), false);
  blocked = true;
  assert.equal(readSidebarCollapsed(), false);
  assert.doesNotThrow(() => saveSidebarCollapsed(true));
});

test('header toggle and both menus share collapse state without a duplicate sidebar trigger', () => {
  const app = readFileSync(new URL('../../src/App.vue', import.meta.url), 'utf8');
  assert.match(app, /<NLayout has-sider/);
  assert.match(app, /<NLayoutSider\s+id="desktop-navigation"\s+:collapsed="sidebarCollapsed"/);
  assert.match(app, /collapse-mode="width"\s+:width="240"\s+:collapsed-width="64"\s+:native-scrollbar="false"/);
  assert.doesNotMatch(app, /show-trigger/);
  assert.match(app, /:aria-expanded="!sidebarCollapsed"\s+aria-controls="desktop-navigation"\s+@click="setSidebarCollapsed\(!sidebarCollapsed\)"/);
  assert.ok(app.indexOf("'展开导航栏'") < app.indexOf('class="app-brand"'));
  assert.match(app, /<AppNavigation :collapsed="sidebarCollapsed"/);
  assert.match(app, /matchMedia\('\(max-width: 1023px\)'\)/);
  assert.match(app, /ref\(sidebarMedia.matches \|\| preferredSidebarCollapsed\)/);
  assert.match(app, /sidebarCollapsed.value = sidebarMedia.matches \|\| preferredSidebarCollapsed/);
  assert.match(app, /sidebarMedia.addEventListener\('change', updateSidebarWidth\)/);
  assert.match(app, /sidebarMedia.removeEventListener\('change', updateSidebarWidth\)/);
  assert.match(app, /saveSidebarCollapsed\(collapsed\)/);
  assert.doesNotMatch(app, /watch\(sidebarCollapsed, saveSidebarCollapsed\)/);
  const menu = readFileSync(new URL('../../src/components/AppNavigation.vue', import.meta.url), 'utf8');
  assert.equal(menu.match(/:collapsed="collapsed"/g).length, 2);
  assert.doesNotMatch(menu, /matchMedia|addEventListener|localStorage/);
  const css = readStylesheet(new URL('../../src/styles/layout.css', import.meta.url));
  assert.match(css, /\.app-sidebar-layout \{ display: none; \}/);
  assert.match(css, /\.app-shell.sidebar-collapsed \{ --sidebar-width: 64px; \}/);
  assert.doesNotMatch(css, /max-width: 1023px/);
});

test('mobile shell uses the shared dropdown navigation and a single content scroller', () => {
  const app = readFileSync(new URL('../../src/App.vue', import.meta.url), 'utf8');
  const menu = readFileSync(new URL('../../src/components/AppNavigation.vue', import.meta.url), 'utf8');
  const css = readStylesheet(new URL('../../src/styles/layout.css', import.meta.url));
  assert.match(app, /matchMedia\('\(min-width: 600px\)'\)/);
  assert.match(app, /<AppNavigation v-else mobile/);
  assert.match(app, /<h1 v-if="!isDesktop" class="mobile-page-title">\s*<span class="mobile-page-title-text">\{\{ pageTitle \}\}<\/span>/);
  assert.match(css, /\.mobile-page-title \{[^}]*display: flex;[^}]*align-items: center;/);
  assert.match(css, /\.mobile-page-title-text \{[^}]*min-width: 0;[^}]*text-overflow: ellipsis;/);
  const scrollers = elements(app, node => node.tag === 'NScrollbar' && node.props.some(prop => prop.type === 6 && prop.name === 'data-page-scroller'));
  assert.equal(scrollers.length, 1);
  assert.equal(attribute(scrollers[0], 'class'), 'app-workspace');
  assert.doesNotMatch(app, /mobile-nav|mobile-menu-backdrop|workspace-header|lockScroll/);
  assert.match(menu, /<NDropdown v-if="mobile" trigger="click" :options="mobileOptions"/);
  assert.match(menu, /<NDropdown[^>]*:value="activeKey"/);
  assert.match(menu, /const activeKey = computed\(\(\) => `\/\$\{route.path.split\('\/'\)\[1\]\}`\)/);
  assert.match(menu, /options.map\(item => \(\{ \.\.\.item, label: item.text \}\)\)/);
  assert.match(css, /grid-template-rows: 60px minmax\(0, 1fr\)/);
  assert.match(css, /@media \(min-width: 600px\)/);
});

test('width changes collapse automatically without overwriting manual preference', () => {
  const media = { matches: false };
  const writes = [];
  const subject = createAppShell({
    window: { matchMedia: () => media },
    saveSidebarCollapsed: value => writes.push(value),
  });
  assert.equal(subject.sidebarCollapsed.value, false);
  media.matches = true;
  subject.updateSidebarWidth();
  assert.equal(subject.sidebarCollapsed.value, true);
  assert.deepEqual(writes, []);
  subject.setSidebarCollapsed(false);
  assert.equal(subject.sidebarCollapsed.value, false);
  media.matches = false;
  subject.updateSidebarWidth();
  assert.equal(subject.sidebarCollapsed.value, false);
  subject.setSidebarCollapsed(true);
  media.matches = true;
  subject.updateSidebarWidth();
  media.matches = false;
  subject.updateSidebarWidth();
  assert.equal(subject.sidebarCollapsed.value, true);
  assert.deepEqual(writes, [false, true]);
});
