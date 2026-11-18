import { readStylesheet, readFeatureStyles } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { attribute, elements } from '../support/template.mjs';

const read = path => path.endsWith('.css') ? readStylesheet(new URL(`../../src/${path}`, import.meta.url)) : readFileSync(new URL(`../../src/${path}`, import.meta.url), 'utf8');

test('drive picker remains one line while the breadcrumb only uses remaining header space', () => {
  const source = read('features/files/FilesView.vue');
  assert.match(source, /\.files-location\s*\{[^}]*flex: 1;/);
  assert.match(source, /<NButton\s+class="files-crumb"/);
  assert.match(source, /\.files-crumb\s*\{[^}]*flex-shrink: 0;/);
  assert.match(source, /\.files-breadcrumb-scrollbar\s*\{[^}]*flex: 1;[^}]*width: auto;[^}]*min-width: 0;/);
  assert.match(source, /\.files-header-actions\s*\{[^}]*flex-shrink: 0;/);
});

test('dialog lists use the edge-aligned modal scrollbar instead of another inset scroll region', () => {
  assert.match(read('features/settings/AvailableCommandsModal.vue'), /body-max-height="65dvh"/);
  assert.match(read('features/tasks/TaskVariableHelp.vue'), /body-max-height="min\(58svh, 480px\)"/);
  for (const path of ['features/settings/AvailableCommandsModal.vue', 'features/tasks/TaskVariableHelp.vue', 'features/plugins/PluginInfoView.vue']) {
    assert.doesNotMatch(read(path), /NScrollbar/);
  }
  const modal = read('components/AppModal.vue');
  assert.match(modal, /content-scrollable/);
  assert.match(modal, /'--dialog-body-max-height': bodyMaxHeight/);
  assert.match(read('features/files/FilesView.vue'), /<NScrollbar class="file-target-scrollbar" content-class="file-target-list">/);
  const styles = readFeatureStyles();
  for (const name of ['variable-help-content', 'status-display-options', 'modal-content']) {
    assert.doesNotMatch(styles.match(new RegExp(`\\.${name}\\s*\\{([^}]+)\\}`))?.[1] ?? '', /overflow[^:]*:\s*auto/);
  }
  assert.doesNotMatch(styles, /\.status-display-scrollbar/);
  assert.match(modal, /\.app-modal > \.n-card__content-scrollbar\s*\{[^}]*max-height: calc\(var\(--dialog-body-max-height, 100svh\)/);
});

test('horizontal navigation and log details use library scrollbars without replacing terminal or editor scrolling', () => {
  const files = read('features/files/FilesView.vue');
  for (const className of ['files-breadcrumb-scrollbar', 'file-target-path']) {
    const [scrollbar] = elements(files, node => node.tag === 'NScrollbar' && attribute(node, 'class') === className);
    assert.ok(scrollbar?.props.some(prop => prop.type === 6 && prop.name === 'x-scrollable'), className);
  }
  assert.match(files, /@scroll="syncEditLines"/);
  assert.match(read('features/logs/LogsView.vue'), /<NScrollbar class="log-details-scrollbar" x-scrollable>/);
  const terminal = read('features/terminal/TerminalView.vue');
  assert.match(terminal, /<NTabs[^>]*class="terminal-tabs"/);
  assert.doesNotMatch(terminal, /tabStrip|scrollActiveTabIntoView|scrollIntoView\(/);
  assert.match(terminal, /'--scrollbar-color': darkTheme.common.scrollbarColor/);
});

test('native scrolling surfaces inherit library theme tokens instead of default browser chrome', () => {
  const source = read('components/AppThemeTokens.vue');
  for (const token of ['scrollbarColor', 'scrollbarColorHover', 'scrollbarWidth', 'scrollbarHeight', 'scrollbarBorderRadius']) {
    assert.ok(source.includes(`'${token}'`));
  }
  const styles = read('styles/scrollbars.css');
  assert.match(styles, /scrollbar-color: var\(--scrollbar-color\) transparent/);
  assert.match(styles, /width: var\(--scrollbar-width\)/);
  assert.match(styles, /height: var\(--scrollbar-height\)/);
  assert.doesNotMatch(styles, /!important|display:\s*none/);
  assert.match(read('main.ts'), /import '.\/style.css'/);
  const entry = readFileSync(new URL('../../src/style.css', import.meta.url), 'utf8');
  assert.match(entry, /@import '.\/styles\/scrollbars.css'/);
});
