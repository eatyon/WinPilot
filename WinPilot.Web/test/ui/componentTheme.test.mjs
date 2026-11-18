import { readStylesheet, readFeatureStyles } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { effectScope, nextTick, ref, watchEffect } from 'vue';
import { darkTheme, lightTheme } from 'naive-ui';
import { loadScriptModule } from '../support/scriptModule.mjs';
import { createComponentTheme } from '../../src/ui/componentTheme.ts';
import { accentOptions, getAccentTokens } from '../../src/ui/themeColors.ts';
import { elements, attribute, binding } from '../support/template.mjs';

test('mobile controls use 16px without changing desktop typography or control heights', () => {
  for (const mode of ['light', 'dark']) {
    const tokens = getAccentTokens(accentOptions[0].value, mode);
    const desktop = createComponentTheme(tokens, mode);
    const mobile = createComponentTheme(tokens, mode, true);
    for (const component of ['Button', 'Input', 'InternalSelection', 'InternalSelectMenu']) {
      assert.equal(desktop[component], undefined);
      for (const size of ['Tiny', 'Small', 'Medium', 'Large']) {
        assert.equal(mobile[component]['fontSize' + size], '16px');
      }
      assert.equal(mobile[component].heightMedium, undefined);
    }
    assert.equal(mobile.common.fontSize, undefined);
  }
  const source = readFileSync(new URL('../../src/components/AppTheme.vue', import.meta.url), 'utf8');
  assert.match(source, /matchMedia\('\(max-width: 599px\)'\)/);
  assert.match(source, /onScopeDispose\(\(\) => mobileMedia.removeEventListener\('change', updateMobile\)\)/);
});

test('plugin panel delegates its background, radius and separator to the library card', () => {
  const styles = readFeatureStyles();
  assert.doesNotMatch(styles, /\.plugins-heading\b|\.plugin-list-summary::before/);
  assert.doesNotMatch(styles.match(/\.plugin-list-summary\s*\{([^}]+)\}/)?.[1] ?? '', /background|border|box-shadow|margin-top/);
  for (const file of ['PluginsView.vue', 'PluginMarketView.vue']) {
    const source = readFileSync(new URL(`../../src/features/plugins/${file}`, import.meta.url), 'utf8');
    assert.match(source, /<template #header>/);
    assert.match(source, /<template #header-extra>/);
    assert.match(source, /:segmented="\{ content: true \}"/);
    assert.match(source, /<NText tag="h2" :depth="1">/);
  }
});

test('component theme inherits library typography and sizes while preserving accents and radius', () => {
  for (const { value } of accentOptions) {
    for (const mode of ['light', 'dark']) {
      const tokens = getAccentTokens(value, mode);
      const primaryColor = tokens.text;
      const theme = createComponentTheme(tokens, mode);
      assert.equal(theme.common.bodyColor, mode === 'light' ? '#f5f5f5' : undefined);
      for (const key of ['heightMedium', 'fontSize', 'fontSizeMedium', 'fontWeightStrong']) {
        assert.equal(theme.common[key], undefined);
      }
      assert.equal(lightTheme.common.heightMedium, '34px');
      assert.equal(lightTheme.common.fontSizeMedium, '14px');
      assert.equal(lightTheme.common.fontWeightStrong, '500');
      assert.equal(theme.common.borderRadius, '10px');
      assert.equal(theme.common.borderRadiusSmall, undefined);
      const commonTheme = (mode === 'dark' ? darkTheme : lightTheme).common;
      assert.equal(theme.Tooltip.color, commonTheme.popoverColor);
      assert.equal(theme.Tooltip.textColor, commonTheme.textColor2);
      assert.equal(theme.Card.colorEmbedded, 'var(--surface-subtle)');
      assert.equal(theme.Card.colorEmbeddedModal, `oklch(from ${commonTheme.modalColor} calc(l - 0.02) c h)`);
      assert.equal(theme.Card.colorEmbeddedPopover, `oklch(from ${commonTheme.popoverColor} calc(l - 0.02) c h)`);
      for (const component of ['Card', 'Dialog', 'Drawer']) {
        assert.equal(theme[component].closeSize, undefined);
        assert.equal(theme[component].closeBorderRadius, theme.common.borderRadius);
        assert.equal(theme[component].closeIconSize, undefined);
      }
      for (const key of ['primaryColor', 'primaryColorPressed']) {
        assert.equal(theme.common[key], primaryColor);
      }
      assert.equal(theme.common.primaryColorHover, tokens.textHover);
      assert.equal(theme.common.primaryColorSuppl, tokens.solid);
      assert.equal(theme.Button, undefined);
      assert.equal(theme.Layout.headerColor, mode === 'dark' ? '#1a1a1f' : '#fff');
      assert.equal(theme.Layout.siderColor, theme.Layout.headerColor);
      assert.equal(theme.Checkbox, undefined);
      assert.equal(theme.Select, undefined);
      const baseTheme = mode === 'dark' ? darkTheme : lightTheme;
      const common = { ...baseTheme.common, ...theme.common };
      const selectMenu = baseTheme.Select.peers.InternalSelectMenu.self(common);
      assert.equal(selectMenu.optionTextColorActive, primaryColor);
      assert.equal(selectMenu.optionCheckColor, primaryColor);
      assert.equal(selectMenu.optionColorActive, 'rgba(0, 0, 0, 0)');
      assert.equal(selectMenu.optionColorActivePending, common.hoverColor);
      const button = baseTheme.Button.self(common);
      assert.equal(button.colorPrimary, primaryColor);
      assert.equal(button.colorHoverPrimary, tokens.textHover);
      assert.equal(button.textColorPrimary, common.baseColor);
      assert.deepEqual(Object.keys(theme.Menu), ['borderRadius']);
    }
  }
});

test('list controls delegate hover focus and expanded appearance to library buttons', () => {
  const styles = readFeatureStyles();
  assert.doesNotMatch(styles, /\.process-count:hover|\.process-count\[aria-expanded/);
  const programs = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  const countButton = elements(programs, node => node.tag === 'NButton' && attribute(node, 'class') === 'process-count')[0];
  assert.equal(binding(countButton, 'type'), "isExpanded(group.id) ? 'primary' : 'default'");
  assert.equal(binding(countButton, 'secondary'), 'isExpanded(group.id)');
  const files = readFileSync(new URL('../../src/features/files/FilesView.vue', import.meta.url), 'utf8');
  assert.match(files, /<NButton\s+class="files-crumb"/);
  assert.doesNotMatch(files, /\.files-crumb:focus-visible|\.files-crumb:hover/);
  assert.match(files, /<NButton[^>]*class="file-root"/);
  assert.ok(elements(files, node => node.tag === 'NButton' && attribute(node, 'class') === 'file-target-button').length);
  assert.doesNotMatch(files, /\.file-root:hover|\.file-target-list button:hover/);
});

test('headings use medium weight while form labels and content stay regular', () => {
  const styles = readStylesheet(new URL('../../src/styles/components.css', import.meta.url));
  assert.match(styles, /:where\(h1, h2, h3, h4, h5, h6, strong, b\)\s*\{ font-weight: 500; \}/);
  assert.doesNotMatch(styles, /\.form-field[^{}]*\{[^}]*font-weight/);
  assert.doesNotMatch(styles, /\.n-form-item-label[^{}]*\{[^}]*font-weight/);
  assert.doesNotMatch(styles.match(/\.form-field\s*\{([^}]+)\}/)?.[1] ?? '', /font-weight/);
  const host = readFeatureStyles();
  assert.match(host, /\.task-config-heading > strong\s*\{ font-weight: 500/);
  for (const selector of ['.login-brand', '.login-copy h1']) {
    const rule = host.slice(host.indexOf(`${selector} {`)).split('}')[0];
    assert.match(rule, /font-weight: 600/);
  }
  for (const selector of ['.modal-program-name']) {
    const rule = host.slice(host.indexOf(`${selector} {`)).split('}')[0];
    assert.match(rule, /font-weight: 500/);
  }
  const tasks = readFileSync(new URL('../../src/features/tasks/TasksView.vue', import.meta.url), 'utf8');
  assert.match(tasks, /<NButton class="task-name-button" text strong size="large"/);
});

test('host semantic colors follow the actual provider theme and release their global overrides', async () => {
  const theme = ref({ ...lightTheme.common, bodyColor: '#f5f5f5' });
  const values = new Map();
  let cleanup;
  const scope = effectScope();
  const context = {
    exports: {}, document: { documentElement: { style: {
      setProperty: (name, value) => values.set(name, value), removeProperty: name => values.delete(name),
    } } },
    require: name => name === 'vue' ? { watchEffect, onBeforeUnmount: callback => { cleanup = callback; } } : { useThemeVars: () => theme },
  };
  try {
    scope.run(() => runInNewContext(loadScriptModule(new URL('../../src/components/AppThemeTokens.vue', import.meta.url)), context));
    assert.equal(values.get('--canvas'), '#f5f5f5');
    assert.equal(values.get('--surface-subtle'), `oklch(from ${lightTheme.common.cardColor} calc(l - 0.02) c h)`);
    assert.notEqual(values.get('--surface-subtle'), values.get('--surface'));
    theme.value = { ...darkTheme.common, ...createComponentTheme(getAccentTokens('blue', 'dark'), 'dark').common };
    await nextTick();
    assert.equal(values.get('--canvas'), darkTheme.common.bodyColor);
    assert.equal(values.get('--surface'), darkTheme.common.cardColor);
    assert.equal(values.get('--surface-subtle'), `oklch(from ${darkTheme.common.cardColor} calc(l - 0.02) c h)`);
    assert.notEqual(values.get('--surface-subtle'), values.get('--surface'));
    assert.equal(values.get('--text-muted'), darkTheme.common.textColor2);
    assert.equal(values.get('--border'), darkTheme.common.dividerColor);
    assert.equal(values.get('--control-height'), '34px');
    assert.equal(values.get('--control-font-size'), '14px');
    assert.equal(values.get('--control-radius'), '10px');
    assert.equal(values.get('--accent-text'), '#4aa3e8');
    assert.equal(values.get('--accent-solid'), '#1c77c7');
    assert.equal(values.get('--accent-contrast'), darkTheme.common.baseColor);
    assert.equal(values.get('--accent-bg'), darkTheme.common.hoverColor);
    assert.equal(values.get('--danger-text'), darkTheme.common.errorColor);
    assert.equal(values.get('--warning-text'), darkTheme.common.warningColor);
    assert.equal(values.get('--success-text'), darkTheme.common.successColor);
    assert.equal(values.size, 38);
    const provider = readFileSync(new URL('../../src/components/AppTheme.vue', import.meta.url), 'utf8');
    assert.match(provider, /<NConfigProvider[^>]*>\s*<AppThemeTokens\s*\/>/);
  } finally { scope.stop(); cleanup?.(); }
  assert.equal(values.size, 0);
});

test('theme entry uses reference body fonts, library preflight and layout transitions instead of per-surface animation rules', () => {
  const provider = readFileSync(new URL('../../src/components/AppTheme.vue', import.meta.url), 'utf8');
  const app = readFileSync(new URL('../../src/App.vue', import.meta.url), 'utf8');
  const entry = readFileSync(new URL('../../src/main.ts', import.meta.url), 'utf8');
  const styles = readStylesheet(new URL('../../src/styles/base.css', import.meta.url));
  assert.match(styles, /body\s*\{[^}]*font-family: 'Lato', 'Fira Code', sans-serif/);
  assert.doesNotMatch(entry, /themeTransitions/);
  assert.match(entry, /import 'vfonts\/Lato\.css'/);
  assert.match(entry, /import 'vfonts\/FiraCode\.css'/);
  assert.doesNotMatch(provider, /preflight-style-disabled/);
  assert.match(provider, /darkTheme : lightTheme/);
  assert.match(app, /<NLayoutHeader class="app-header" role="banner">/);
  const theme = createComponentTheme(getAccentTokens('blue', 'light'));
  assert.equal(theme.common.fontFamily, undefined);
  assert.equal(theme.common.fontFamilyMono, undefined);
});
