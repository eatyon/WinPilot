import { readStylesheet, readFeatureStyles } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { compileStyle, parse } from '@vue/compiler-sfc';
import { elements, attribute, binding } from '../support/template.mjs';

test('plugin configuration display separates the visibility option from configuration fields with library spacing', () => {
  const source = readFileSync(new URL('../../src/features/plugins/PluginInfoView.vue', import.meta.url), 'utf8');
  const [space] = elements(source, node => node.tag === 'NSpace' && attribute(node, 'class') === 'plugin-configuration-display');
  assert.ok(space);
  assert.ok(space.props.some(prop => prop.type === 6 && prop.name === 'vertical'));
  assert.equal(binding(space, 'size'), '16');
  assert.equal(binding(space, 'inert'), 'savingSettings');
  const children = space.children.filter(node => node.type === 1);
  assert.deepEqual(children.map(node => node.tag), ['NCheckbox', 'div']);
  assert.equal(attribute(children[1], 'class'), 'plugin-settings-fields');
});

test('program and Cron explanation icons share a compact centered label row', () => {
  const css = readStylesheet(new URL('../../src/styles/components.css', import.meta.url));
  assert.match(css, /\.field-label-with-hint \{ display: inline-flex; align-items: center; gap: 2px; \}/);
  for (const file of ['programs/ProgramsView.vue', 'tasks/CronEditor.vue']) {
    const source = readFileSync(new URL(`../../src/features/${file}`, import.meta.url), 'utf8');
    const labels = elements(source, node => (attribute(node, 'class') ?? '').split(/\s+/).includes('field-label-with-hint'));
    assert.ok(labels.some(node => node.children.some(child => child.tag === 'span')
      && node.children.some(child => child.tag === 'HelpHint')), file);
  }
  const cron = readFileSync(new URL('../../src/features/tasks/CronEditor.vue', import.meta.url), 'utf8');
  assert.doesNotMatch(cron, /cron-expression-label|help-hint-popover/);
});

test('plugin headings and configuration labels use library typography without a second color override', () => {
  const header = readFileSync(new URL('../../src/features/plugins/PluginHeader.vue', import.meta.url), 'utf8');
  const fields = readFileSync(new URL('../../src/features/plugins/PluginSettingField.vue', import.meta.url), 'utf8');
  const css = readFeatureStyles();
  assert.match(header, /<NText tag="h2" :depth="1">/);
  assert.match(header, /<NText tag="p" :depth="2">/);
  assert.match(fields, /<NText :depth="2">/);
  assert.match(fields, /<NText v-if="field.description" tag="small" :depth="2">/);
  assert.doesNotMatch(css.match(/\.plugin-setting-label > span\s*\{([^}]+)\}/)?.[1] ?? '', /(?:^|;)\s*color:/);
});

test('plugin field containers do not make controls bold while field titles retain emphasis', () => {
  const css = readFeatureStyles();
  const field = css.match(/\.plugin-setting-field\s*\{([^}]+)\}/)?.[1];
  assert.ok(field);
  assert.doesNotMatch(field, /font-weight/);
  assert.match(css, /\.plugin-setting-label > span\s*\{[^}]*font-weight:\s*400;/);
});

test('task and dynamic plugin channel pickers use the shared default prompt', () => {
  const action = readFileSync(new URL('../../src/features/tasks/TaskActionEditor.vue', import.meta.url), 'utf8');
  const plugin = readFileSync(new URL('../../src/features/plugins/PluginSettingField.vue', import.meta.url), 'utf8');
  assert.match(action, /<NFormItem\s+label="通知渠道"[\s\S]*?data-task-field="notificationChannel"/);
  assert.doesNotMatch(action, /placeholder="请选择通知渠道"/);
  assert.doesNotMatch(action, /\{ value: '', label: '请选择通知渠道' \}/);
  assert.doesNotMatch(plugin, /:placeholder=/);
  const select = readFileSync(new URL('../../src/components/AppSelect.vue', import.meta.url), 'utf8');
  assert.match(select, /placeholder: '请选择'/);
  assert.doesNotMatch(plugin, /options.unshift/);
});

test('plugin and task multiline fields use the library default rows and remain resizable', () => {
  for (const file of ['plugins/PluginSettingField.vue', 'tasks/TaskActionEditor.vue', 'tasks/TasksView.vue']) {
    const source = readFileSync(new URL(`../../src/features/${file}`, import.meta.url), 'utf8');
    assert.match(source, /type="textarea"/);
    assert.doesNotMatch(source, /\brows:\s*['"]\d|:rows="\d/);
    assert.doesNotMatch(source, /:resizable="false"/);
  }
});

test('task list keeps library card padding and avoids doubling the last row spacing', () => {
  const source = readFileSync(new URL('../../src/features/tasks/TasksView.vue', import.meta.url), 'utf8');
  const css = readStylesheet(new URL('../../src/styles/components.css', import.meta.url));
  assert.match(source, /<NCard tag="section" class="task-list">/);
  assert.doesNotMatch(css, /task-list-content/);
  const layout = readFeatureStyles();
  assert.match(layout, /\.task-list \.task-row:last-of-type \{ padding-bottom: 0; \}/);
  for (const rule of layout.matchAll(/\.task-list\s*\{([^}]+)\}/g)) assert.doesNotMatch(rule[1], /padding/);
});

test('pagination keeps its page size label beside the control on both devices', () => {
  const css = readStylesheet(new URL('../../src/styles/components.css', import.meta.url));
  assert.match(css, /\.app-pagination \.page-size-control\s*\{[^}]*grid-template-columns: auto 76px;[^}]*align-items: center;[^}]*width: auto;[^}]*flex: none/);
});

test('desktop login restores its original padding without changing the mobile layout', () => {
  const css = readFeatureStyles();
  assert.match(css, /@media \(min-width: 641px\)\s*\{[^}]*\}\s*\.login-view \.login-panel > \.login-panel-content:first-child \{ padding: 36px; \}/);
  assert.match(css, /@media \(max-width: 640px\)[\s\S]*\.login-view \.login-panel > \.login-panel-content:first-child \{ padding: 0; \}/);
});

test('task action text buttons retain spacing without relying on button padding', () => {
  const css = readFeatureStyles();
  assert.match(css, /\.task-config-heading > div\s*\{[^}]*display:\s*flex;[^}]*align-items:\s*center;[^}]*gap:\s*8px/);
});

test('terminal controls use a local dark theme and the command input starts at one row', () => {
  const source = readFileSync(new URL('../../src/features/terminal/TerminalView.vue', import.meta.url), 'utf8');
  assert.match(source, /<NConfigProvider\s+v-if="activeTab"\s+class="terminal-panel"\s+:theme="darkTheme"[^>]*>/);
  assert.match(source, /:rows="1"\s+:resizable="false"/);
  assert.doesNotMatch(source, /input-props="\{ rows:/);
  assert.match(source, /<NTabs[^>]*type="card"\s+closable[^>]*@close="closeTab"/);
  const tab = elements(source, node => node.tag === 'NTab' && binding(node, 'name') === 'tab.id')[0];
  assert.ok(tab);
  assert.equal(tab.props.find(prop => prop.type === 7 && prop.name === 'for')?.exp?.content, 'tab in tabs');
});

test('desktop terminal fills its page container and keeps the shared preview background and dashed border', () => {
  const source = readFileSync(new URL('../../src/features/terminal/TerminalView.vue', import.meta.url), 'utf8');
  assert.match(source, /\.terminal-view\s*\{[^}]*display: flex;[^}]*min-width: 0;[^}]*flex-direction: column;/);
  assert.match(source, /\.terminal-view > \.n-card, \.terminal-tabs \{ flex-shrink: 0; \}/);
  const layout = readStylesheet(new URL('../../src/features/terminal/TerminalView.vue', import.meta.url));
  assert.doesNotMatch(source, /100vh - 240px|min-height: 680px/);
  assert.match(source, /<NCard\s+v-else\s+class="terminal-empty"[^>]*color-mix\(in srgb, var\(--canvas\) 90%, var\(--surface\) 10%\)/);
  assert.match(source, /\.terminal-empty\s*\{[^}]*border-style: dashed;/);
  assert.match(layout, /\.page-content:has\(\.terminal-view\) > \.route-page \{ display: flex; flex: 1; min-height: 0; \}/);
  assert.match(source, /\.terminal-panel, \.terminal-empty\s*\{\s*flex: 1 0 320px;\s*min-height: 440px;/);
});

test('shared form fields and task form content constrain intrinsic select widths', () => {
  const css = readStylesheet(new URL('../../src/styles/components.css', import.meta.url));
  assert.match(css, /\.form-field\s*\{[^}]*min-width:\s*0/);
  const tasks = readStylesheet(new URL('../../src/features/tasks/TasksView.vue', import.meta.url));
  assert.match(tasks, /\.form-field\.n-form-item > \.form-field-content\s*\{[^}]*grid-template-columns:\s*minmax\(0, 1fr\)/);
});

test('custom themed surfaces delegate their card shell and transitions to NCard', () => {
  const read = path => path.endsWith('.css') ? readStylesheet(new URL(`../../src/${path}`, import.meta.url)) : readFileSync(new URL(`../../src/${path}`, import.meta.url), 'utf8');
  for (const [path, pattern] of [
    ['features/desktop/DesktopView.vue', /<NCard\s+class="desktop-frame"/],
    ['features/terminal/TerminalView.vue', /<NCard class="terminal-tabs-card"/],
    ['features/terminal/TerminalView.vue', /<NCard\s+v-else\s+class="terminal-empty"/],
    ['features/plugins/PluginsView.vue', /<NCard\s+v-else\s+embedded\s+class="plugin-empty"/],
    ['features/plugins/PluginSettingField.vue', /<NCard embedded\b[^>]*\bcontent-class="plugin-setting-field"/],
    ['features/logs/LogsView.vue', /<NCard[^>]*class="logs-empty-card"/],
  ]) assert.match(read(path), pattern, path);
  const styles = readFeatureStyles();
  const marketEmptyCard = elements(read('features/plugins/PluginMarketView.vue'), node => node.tag === 'NCard')
    .find(card => card.children.some(child => child.type === 1 && child.tag === 'NEmpty'));
  assert.ok(marketEmptyCard);
  assert.equal(binding(marketEmptyCard, 'content-style'), undefined);
  for (const selector of ['.desktop-frame', '.plugin-setting-field', '.plugin-card']) {
    for (const match of styles.matchAll(new RegExp(`\\${selector}\\s*\\{([^}]+)\\}`, 'g'))) {
      assert.doesNotMatch(match[1], /(?:^|;)\s*(?:background|border-radius|box-shadow)\s*:/, selector);
      const transition = match[1].match(/(?:^|;)\s*transition\s*:\s*([^;]+)/)?.[1];
      if (transition) {
        assert.match(transition, /^color\s/, selector);
        assert.doesNotMatch(transition, /(?:^|,)\s*(?:all|background(?:-color)?|border(?:-color)?|box-shadow)\b/, selector);
      }
    }
  }
  const programs = read('features/programs/ProgramsView.vue');
  const usage = programs.slice(programs.indexOf('class="usage-grid"'), programs.indexOf('<template #footer>', programs.indexOf('class="usage-grid"')));
  assert.equal((usage.match(/<NCard embedded/g) ?? []).length, 3);
});

test('scoped page layouts reach the public card content containers', () => {
  for (const [file, page, content] of [
    ['logs/LogsView.vue', 'logs-view', 'logs-filters'],
    ['terminal/TerminalView.vue', 'terminal-view', 'terminal-toolbar'],
    ['plugins/PluginPageView.vue', 'plugin-feature-page-view', 'plugin-feature-page-heading'],
    ['terminal/TerminalView.vue', 'terminal-view', 'terminal-empty-content'],
  ]) {
    const { descriptor } = parse(readFileSync(new URL(`../../src/features/${file}`, import.meta.url), 'utf8'));
    const style = descriptor.styles.find(item => item.scoped);
    const result = compileStyle({ source: style.content, id: 'data-v-layout', scoped: true });
    assert.deepEqual(result.errors, []);
    assert.ok(result.code.includes(`.${page}[data-v-layout] .${content} {`), file);
    assert.ok(!result.code.includes(`.${content}[data-v-layout]`), file);
  }
});
