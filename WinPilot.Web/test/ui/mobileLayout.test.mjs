import { readStylesheet, readFeatureStyles } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { compileStyle, parse } from '@vue/compiler-sfc';

const read = path => path.endsWith('.css') ? readStylesheet(new URL(`../../src/${path}`, import.meta.url)) : readFileSync(new URL(`../../src/${path}`, import.meta.url), 'utf8');

test('shared page padding adapts to narrow screens without changing desktop spacing', () => {
  assert.match(read('styles/layout.css'), /\.page-content\s*\{[^}]*padding: clamp\(16px, 3vw, 24px\)/);
});

test('all page content shares the same centered width limit', () => {
  const css = read('styles/layout.css');
  assert.doesNotMatch(css.match(/\.page-content\s*\{([^}]+)\}/)[1], /max-width/);
  assert.match(css, /\.page-content > \.route-page\s*\{[^}]*max-width: 1440px;[^}]*margin-inline: auto;/);
  assert.doesNotMatch(css, /\.page-content > \.route-page[^{}]*\{[^}]*max-width: none;/);
});

test('mobile task creation button fills the available row after wrapping', () => {
  assert.match(readFeatureStyles(), /\.task-list \.content-section-header > \.content-actions > \.n-button\s*\{ flex-grow: 1; \}/);
});

test('mobile layouts rearrange existing program, task and plugin containers', () => {
  const css = readFeatureStyles();
  const mobile = css.slice(css.indexOf('@media (max-width: 767px)'));
  assert.match(mobile, /\.programs-view \.content-section-header\s*\{[^}]*flex-direction: column/);
  assert.match(mobile, /\.process-query-actions \.user-select\s*\{[^}]*width: 100%/);
  const programs = read('features/programs/ProgramsView.vue');
  assert.match(programs, /grid-template-areas: 'label' 'blank' 'feedback'/);
  assert.match(programs, /\.user-select :deep\(\.user-select-label\)\s*\{[^}]*justify-self: start/);
  assert.match(mobile, /\.task-config-heading, \.task-config-heading > div\s*\{ flex-wrap: wrap; \}/);
  assert.match(mobile, /\.plugin-card-footer\s*\{ flex-wrap: wrap; margin-top: 18px; \}/);
});

test('plugin grids allow long configuration content to shrink within the viewport', () => {
  const css = readFeatureStyles();
  for (const selector of ['.plugins-view, .plugin-detail-view', '.plugin-home-view']) {
    const rule = css.slice(css.indexOf(`${selector} {`)).split('}')[0];
    assert.match(rule, /grid-template-columns: minmax\(0, 1fr\)/);
    assert.match(rule, /min-width: 0/);
  }
  assert.match(css, /\.plugin-module, \.plugin-settings-fields, \.plugin-setting-field, \.plugin-collection-item-fields\s*\{[^}]*min-width: 0;[^}]*grid-template-columns: minmax\(0, 1fr\)/);
});

test('shared refresh actions use a full mobile row through scoped component styling', () => {
  const { descriptor } = parse(read('components/RefreshToolbar.vue'));
  const style = descriptor.styles.find(item => item.scoped);
  const result = compileStyle({ source: style.content, id: 'data-v-mobile', scoped: true });
  assert.deepEqual(result.errors, []);
  assert.match(result.code, /@media \(max-width: 767px\)\s*\{\s*\.refresh-actions\[data-v-mobile\]\s*\{ width: 100%; justify-content: space-between;/);
});

test('file panel retains the library content padding on both desktop and mobile', () => {
  const { descriptor } = parse(read('features/files/FilesView.vue'));
  const result = compileStyle({ source: descriptor.styles[0].content, id: 'file-layout' });
  assert.deepEqual(result.errors, []);
  for (const rule of result.code.matchAll(/[^{}]*\.files-panel\s*\{([^}]+)\}/g)) assert.doesNotMatch(rule[1], /padding/);
  assert.match(descriptor.template.content, /<NCard tag="section" content-class="files-panel">/);
});

test('mobile file metadata aligns right and reserves the full timestamp width', () => {
  const source = read('features/files/FilesView.vue');
  const mobile = source.slice(source.indexOf('@media (max-width: 767px)'));
  assert.match(mobile, /--file-list-columns: 30px minmax\(0, 1fr\) max-content/);
  assert.match(mobile, /\.file-list-head, \.file-row\s*\{[^}]*padding-right: 8px/);
  assert.doesNotMatch(mobile, /padding: (?:8|12)px 0;/);
  for (const selector of ['.file-list-head span:nth-of-type(3)', '.file-list-head span:nth-of-type(5)', '.file-type', '.file-time']) {
    const start = mobile.lastIndexOf(`${selector} {`);
    assert.notEqual(start, -1);
    assert.match(mobile.slice(start).split('}')[0], /text-align: right/);
  }
  assert.match(mobile, /\.file-time\s*\{[^}]*overflow: visible;[^}]*font-variant-numeric: tabular-nums/);
});

test('pagination wraps the library control as a whole only when space is insufficient', () => {
  const css = read('styles/components.css');
  assert.match(css, /\.app-pagination\s*\{[^}]*flex-flow: row wrap/);
  assert.match(css, /\.app-pagination > \.n-pagination\s*\{ flex: none; \}/);
  for (const file of ['features/files/FilesView.vue', 'features/programs/ProgramsView.vue']) {
    assert.match(read(file), /class="app-pagination"/);
    assert.doesNotMatch(read(file), /program-pagination|file-pagination|pagination-actions/);
  }
});

test('terminal shares a growing viewport layout and keeps the mobile send button on the input row', () => {
  const source = read('features/terminal/TerminalView.vue');
  assert.doesNotMatch(source, /100vh - 170px|min-height: 420px|grid-row:/);
  assert.match(readStylesheet(new URL('../../src/features/terminal/TerminalView.vue', import.meta.url)), /\.page-content:has\(\.terminal-view\) \{ display: flex; min-height: 100%; flex-direction: column; \}/);
  const inputRules = [...source.matchAll(/\.terminal-input\s*\{([^}]+)\}/g)];
  assert.match(inputRules[0][1], /grid-template-columns: auto minmax\(0, 1fr\) auto;/);
  for (const rule of inputRules.slice(1)) assert.doesNotMatch(rule[1], /grid-template-columns/);
  const mobile = source.slice(source.indexOf('@media (max-width: 767px)'));
  assert.doesNotMatch(mobile.split('.terminal-toolbar .form-field')[0], /padding:/);
  assert.doesNotMatch(read('features/plugins/PluginsView.vue'), /:content-style="\{ paddingTop: '14px', paddingBottom: '14px' \}"/);
});
