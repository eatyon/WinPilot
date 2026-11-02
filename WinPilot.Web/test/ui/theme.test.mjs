import { readStylesheet, readFeatureStyles } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, readdirSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';
import { computed, ref } from 'vue';
import { darkTheme, lightTheme } from 'naive-ui';
import { accentOptions, getAccentTokens } from '../../src/ui/themeColors.ts';
import { elements, attribute, binding } from '../support/template.mjs';

const properties = {
  text: '--accent-text', textHover: '--accent-text-hover',
  solid: '--accent-solid', solidHover: '--accent-solid-hover', contrast: '--accent-contrast',
  bg: '--accent-bg', border: '--accent-border', focusRing: '--accent-focus-ring',
};

test('theme switching delegates component colors to the provider without changing native color-scheme', () => {
  const theme = readFileSync(new URL('../../src/ui/theme.ts', import.meta.url), 'utf8');
  const css = readFeatureStyles();
  const html = readFileSync(new URL('../../index.html', import.meta.url), 'utf8');
  assert.doesNotMatch(theme, /style\.colorScheme|setProperty\(['"]color-scheme/);
  assert.doesNotMatch(css + html, /(?:^|[;{\s])color-scheme\s*:/);
  assert.match(theme, /window\.matchMedia\('\(prefers-color-scheme: dark\)'\)/);
  assert.match(html, /@media \(prefers-color-scheme: dark\)[\s\S]*html:not\(\[data-theme="light"\]\)/);
});

test('login and about gradients derive their tint from the active theme accent', () => {
  const css = readFeatureStyles();
  assert.match(css, /\.login-view\s*\{[^}]*var\(--brand-accent\) 12%, var\(--brand-canvas\)/);
  assert.match(css, /\.n-card\.about-product-section[^\n]*var\(--brand-accent\) 12%, var\(--brand-surface\)/);
  const brand = readFileSync(new URL('../../src/styles/brand.css', import.meta.url), 'utf8');
  assert.match(brand, /--brand-accent:\s*var\(--accent-solid\)/);
  assert.match(brand, /--brand-surface:\s*var\(--surface\)/);
});

test('help buttons use library keyboard focus styling', () => {
  const source = readFileSync(new URL('../../src/components/HelpHint.vue', import.meta.url), 'utf8');
  assert.match(source, /NButton/);
  assert.doesNotMatch(source, /outline:|box-shadow:/);
});

test('appearance choices delegate radio keyboard navigation and focus to the library', () => {
  const source = readFileSync(new URL('../../src/components/AppearanceSettingsDrawer.vue', import.meta.url), 'utf8');
  assert.equal((source.match(/<NRadioGroup/g) ?? []).length, 1);
  assert.match(source, /<NSwitch :value="themeMode === 'system'"/);
  assert.match(source, /<NSwitch :value="resolvedTheme === 'dark'"/);
  assert.match(source, /setAccent\(value as AccentColor\)/);
  assert.doesNotMatch(source, /tabindex=|@keydown|type="radio"/);
});

test('loading, disabled and global focus visuals belong to the shared library', () => {
  const css = readFeatureStyles();
  assert.doesNotMatch(css, /\.button(?:[.:\s{]|-primary|-secondary|-danger)|\.is-pending/);
  assert.doesNotMatch(css, /:where\(:focus-visible\)/);
  const plugins = readFileSync(new URL('../../src/features/plugins/PluginsView.vue', import.meta.url), 'utf8');
  const scan = elements(plugins, node => node.tag === 'NButton' && attribute(node, 'aria-label') === '重新扫描')[0];
  assert.ok(scan);
  assert.equal(binding(scan, 'loading'), 'showScanLoading');
  assert.equal(binding(scan, 'disabled'), '(loading && !showScanLoading) || changingIds.size > 0');
  assert.doesNotMatch(css, /safe-mode-button|safe-mode-lock-icon|safe-mode-brand-lock/);
});

test('toast messages use the shared library provider instead of a custom stack', () => {
  const css = readFeatureStyles();
  const provider = readFileSync(new URL('../../src/components/AppTheme.vue', import.meta.url), 'utf8');
  assert.doesNotMatch(css, /\.toast-stack\s*\{/);
  assert.match(provider, /<NMessageProvider placement="top" :duration="4200"/);
});

test('invalid form controls delegate their error state to the shared library', () => {
  const css = readStylesheet(new URL('../../src/style.css', import.meta.url));
  assert.doesNotMatch(css, /\.field-control|\.input-action/);
  assert.doesNotMatch(css, /\.tag-search input|\.plugin-secret-input|\.text-input/);
  const keyboard = readStylesheet(new URL('../../src/components/KeyboardKeyPicker.vue', import.meta.url));
  assert.match(keyboard, /\.selected-keys\[aria-invalid="true"\]/);
  for (const name of ['AppSelect', 'AppMultiSelect']) {
    const source = readFileSync(new URL(`../../src/components/${name}.vue`, import.meta.url), 'utf8');
    assert.match(source, /<NSelect/);
    assert.ok(source.includes(':status="invalid ? \'error\' : undefined"'));
  }
});

function contrast(first, second) {
  const luminance = color => {
    if (/^#[\da-f]{3}$/i.test(color)) color = '#' + [...color.slice(1)].map(value => value.repeat(2)).join('');
    const rgb = (color.startsWith('#')
      ? color.slice(1).match(/../g).map(value => parseInt(value, 16))
      : color.match(/[\d.]+/g).slice(0, 3).map(Number)).map(value => value / 255);
    const linear = rgb.map(value => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);
    return linear[0] * 0.2126 + linear[1] * 0.7152 + linear[2] * 0.0722;
  };
  const a = luminance(first);
  const b = luminance(second);
  return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
}

function fixture(saved = {}, dark = false) {
  const source = readFileSync(new URL('../../src/ui/theme.ts', import.meta.url), 'utf8');
  const code = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
  }).outputText;
  const values = new Map();
  const storage = new Map(Object.entries(saved));
  const listeners = [];
  const root = { dataset: {}, removeAttribute: key => { if (key === 'data-theme') delete root.dataset.theme; }, style: { setProperty: (key, value) => values.set(key, value) } };
  const media = { matches: dark, addEventListener: (type, listener) => listeners.push(listener) };
  const context = {
    exports: {},
    require: name => name === 'vue' ? { computed, ref } : { accentOptions },
    document: { documentElement: root },
    localStorage: { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value) },
    window: { matchMedia: () => media },
  };
  runInNewContext(code, context);
  return { subject: context.exports, root, values, storage, media, listeners };
}

test('all ten choices provide mode-aware emphasis colors and a readable solid button color', () => {
  assert.equal(accentOptions.length, 10);
  assert.equal(new Set(accentOptions.map(option => option.value)).size, 10);
  assert.equal(new Set(accentOptions.map(option => option.label)).size, 10);
  for (const option of accentOptions) {
    assert.ok(contrast(option.color, '#fff') >= 4.5, `${option.value} primary button`);
    for (const mode of ['light', 'dark']) {
      const tokens = getAccentTokens(option.value, mode);
      const common = (mode === 'dark' ? darkTheme : lightTheme).common;
      assert.deepEqual(Object.keys(tokens).sort(), ['solid', 'text', 'textHover']);
      assert.equal(tokens.solid, option.color);
      assert.notEqual(tokens.text, tokens.textHover);
      for (const background of [common.bodyColor, common.cardColor]) {
        assert.ok(contrast(tokens.text, background) >= 4.5, `${option.value} ${mode} emphasis`);
        assert.ok(contrast(tokens.textHover, background) >= 4.5, `${option.value} ${mode} hover`);
      }
    }
    assert.notEqual(getAccentTokens(option.value, 'light').text, getAccentTokens(option.value, 'dark').text);
  }
  for (const mode of ['light', 'dark']) {
    assert.deepEqual(getAccentTokens('unknown', mode), getAccentTokens(accentOptions[0].value, mode));
  }
});

test('host and plugin Vue pages delegate focus to library controls', () => {
  const hostCss = readFeatureStyles();
  assert.doesNotMatch(hostCss, /:where\(:focus-visible\)/);
  const renderer = readFileSync(new URL('../../src/features/plugins/page/PluginPageRenderer.vue', import.meta.url), 'utf8');
  assert.doesNotMatch(renderer, /attachShadow|AppPicker|controlStyles/);
});

test('theme choices persist while only the provider writes color variables', () => {
  const f = fixture({ 'winpilot.theme': 'system', 'winpilot.accent': 'violet' }, true);
  f.subject.initializeTheme();
  f.subject.initializeTheme();
  assert.equal(f.listeners.length, 1);
  const theme = f.subject.useTheme();
  const check = (color, mode) => {
    assert.equal(theme.accentColor.value, color);
    assert.equal(theme.resolvedTheme.value, mode);
    assert.equal(f.values.size, 0);
  };
  check('violet', 'dark');
  for (const { value: color } of accentOptions) {
    theme.setAccent(color);
    for (const mode of ['light', 'dark']) {
      theme.setTheme(mode);
      check(color, mode);
      assert.equal(f.root.dataset.theme, mode);
    }
  }
  theme.setTheme('system');
  f.media.matches = false;
  f.listeners[0]();
  check('amber', 'light');
  assert.equal(f.root.dataset.theme, undefined);
  assert.equal(f.storage.get('winpilot.theme'), 'system');
  assert.equal(f.storage.get('winpilot.accent'), 'amber');
  theme.setTheme('light');
  f.media.matches = true;
  f.listeners[0]();
  check('amber', 'light');
});

test('invalid stored theme and inherited palette keys fall back to defaults', () => {
  for (const value of ['unknown', 'constructor', '__proto__', 'rose', 'coral', 'green', 'orange']) {
    const f = fixture({ 'winpilot.theme': 'unknown', 'winpilot.accent': value });
    f.subject.initializeTheme();
    assert.equal(f.subject.useTheme().accentColor.value, 'blue');
    assert.equal(f.subject.useTheme().themeMode.value, 'system');
    assert.equal(f.values.size, 0);
  }
});

test('host sources only reference defined semantic accent and status variables', () => {
  const allowed = new Set([...Object.values(properties), '--danger-text', '--danger-border', '--danger-bg', '--danger-solid', '--danger-solid-hover', '--danger-contrast', '--success-text', '--success-bg', '--success-border', '--warning-text', '--warning-bg', '--warning-border']);
  const roots = [new URL('../../src/', import.meta.url)];
  const checkDirectory = directory => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const file = new URL(entry.name + (entry.isDirectory() ? '/' : ''), directory);
      if (entry.isDirectory()) checkDirectory(file);
      else if (/\.(css|vue|ts|js)$/.test(entry.name)) {
        const content = readFileSync(file, 'utf8');
        for (const match of content.matchAll(/--(?:accent|danger|success|warning)(?:-[a-z-]+)?\b/g)) {
          assert.ok(allowed.has(match[0]), `${file.pathname}: ${match[0]}`);
        }
      }
    }
  };
  roots.forEach(checkDirectory);
});

test('secret fields use the library password control without a second custom focus ring', () => {
  const css = readFeatureStyles();
  assert.doesNotMatch(css, /\.secret-input|\.secret-toggle|\.login-password-input/);
  for (const file of ['auth/LoginView.vue', 'plugins/PluginSettingField.vue', 'settings/tabs/SecuritySettings.vue']) {
    const source = readFileSync(new URL('../../src/features/' + file, import.meta.url), 'utf8');
    assert.match(source, /<AppPasswordInput/);
  }
  const password = readFileSync(new URL('../../src/components/AppPasswordInput.vue', import.meta.url), 'utf8');
  assert.match(password, /type="password" show-password-on="click"/);
  assert.match(password, /#password-invisible-icon/);
  assert.match(password, /#password-visible-icon/);
  assert.match(password, /aria-label="显示密码"/);
  assert.match(password, /aria-label="隐藏密码"/);
  assert.doesNotMatch(password, /visible =|visible\.value|@click/);
  assert.match(password, /attr-type="button"/);
});

test('status colors follow library tokens rather than handwritten light and dark palettes', () => {
  const css = readFeatureStyles();
  const taskWarning = css.match(/\.task-warning\s*\{([^}]+)\}/)[1];
  assert.doesNotMatch(taskWarning, /color:/);
  const tasks = readFileSync(new URL('../../src/features/tasks/TasksView.vue', import.meta.url), 'utf8');
  assert.equal((tasks.match(/<NText\s+type="warning"\s+class="task-warning"/g) ?? []).length, 2);
  assert.ok(!taskWarning.includes('color-mix'));
  assert.doesNotMatch(css, /--(?:danger|success|warning)-[a-z-]+:\s*#/);
  const provider = readFileSync(new URL('../../src/components/AppThemeTokens.vue', import.meta.url), 'utf8');
  for (const [state, token] of [['success', 'successColor'], ['warning', 'warningColor'], ['danger', 'errorColor']]) {
    assert.ok(provider.includes(`'--${state}-text': '${token}'`));
    assert.ok(provider.includes(`'--${state}-bg': 'hoverColor'`));
  }
});

test('keyboard page spacing belongs to the desktop container rather than the shared picker', () => {
  const styles = readFeatureStyles();
  const desktop = readFileSync(new URL('../../src/features/desktop/DesktopView.vue', import.meta.url), 'utf8');
  const pickerStyle = styles.match(/\.virtual-keyboard\s*\{([^}]*)\}/)?.[1];
  assert.ok(pickerStyle);
  assert.doesNotMatch(pickerStyle, /margin(?:-top)?\s*:/);
  assert.match(styles, /\.keyboard-help\s*\{\s*margin:\s*0;/);
  assert.match(styles, /\.keyboard-panel-controls\s*\{\s*padding-top:\s*25px;/);
  assert.match(styles, /\.keyboard-actions\s*\{[^}]*margin-top:\s*8px;/);
  assert.ok(desktop.includes('class="keyboard-panel-controls"'));
  assert.doesNotMatch(styles, /\.task-config-card[^{}]*\.(?:virtual-keyboard|keyboard-help)\s*\{/);
});

test('field validation appearance belongs to library form feedback rather than a handwritten error class', () => {
  const styles = readFeatureStyles();
  assert.doesNotMatch(styles, /\.field-error\s*\{/);
  for (const file of ['tasks/TasksView.vue', 'tasks/CronEditor.vue', 'plugins/PluginSettingField.vue']) {
    const source = readFileSync(new URL(`../../src/features/${file}`, import.meta.url), 'utf8');
    assert.match(source, /<NFormItem/);
    assert.match(source, /:show-feedback=/);
    assert.match(source, /:validation-status=/);
    assert.doesNotMatch(source, /class="field-error"/);
  }
});

test('host styles do not reference undefined CSS variables without fallbacks', () => {
  const sources = [];
  const collect = directory => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const file = new URL(entry.name + (entry.isDirectory() ? '/' : ''), directory);
      if (entry.isDirectory()) collect(file);
      else if (/\.(css|vue|ts|js)$/.test(entry.name)) sources.push([file, readFileSync(file, 'utf8')]);
    }
  };
  [new URL('../../src/', import.meta.url)].forEach(collect);
  const card = readFileSync(new URL('../../node_modules/naive-ui/es/card/src/Card.mjs', import.meta.url), 'utf8');
  const cardPadding = ['--n-padding-top', '--n-padding-left', '--n-padding-bottom'];
  for (const name of cardPadding) assert.ok(card.includes(`"${name}":`));
  const defined = new Set([...Object.values(properties), '--n-height', ...cardPadding]);
  for (const [file, source] of sources) {
    for (const match of source.matchAll(/['"]?(--[a-z][a-z0-9-]*)['"]?\s*:/g)) defined.add(match[1]);
  }
  for (const [file, source] of sources) {
    for (const match of source.matchAll(/var\((--[a-z0-9-]+)\)/g)) {
      assert.ok(defined.has(match[1]), `${file.pathname}: ${match[1]}`);
    }
  }
});
