import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, readdirSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';
import { ref } from 'vue';
import { accentOptions, accentPalettes, getAccentTokens } from '../../src/ui/themeColors.ts';

const properties = {
  text: '--accent-text', textHover: '--accent-text-hover',
  solid: '--accent-solid', solidHover: '--accent-solid-hover', contrast: '--accent-contrast',
  bg: '--accent-bg', border: '--accent-border', focusRing: '--accent-focus-ring',
};

function contrast(first, second) {
  const luminance = hex => {
    const rgb = hex.slice(1).match(/../g).map(value => parseInt(value, 16) / 255);
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
    require: name => name === 'vue' ? { ref } : { accentPalettes, getAccentTokens },
    document: { documentElement: root },
    localStorage: { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value) },
    window: { matchMedia: () => media },
  };
  runInNewContext(code, context);
  return { subject: context.exports, root, values, storage, media, listeners };
}

test('all ten themes provide complete semantic colors with readable text and solid controls', () => {
  assert.equal(Object.keys(accentPalettes).length, 10);
  assert.deepEqual(accentOptions.map(option => option.value), Object.keys(accentPalettes));
  assert.equal(new Set(accentOptions.map(option => option.value)).size, 10);
  assert.equal(new Set(accentOptions.map(option => option.label)).size, 10);
  for (const [color, palette] of Object.entries(accentPalettes)) {
    for (const mode of ['light', 'dark']) {
      const tokens = getAccentTokens(color, mode);
      assert.deepEqual(Object.keys(tokens).sort(), Object.keys(properties).sort());
      assert.equal(tokens.contrast, '#ffffff');
      assert.ok(contrast(tokens.solid, tokens.contrast) >= 4.5, `${color} ${mode} button`);
      assert.ok(contrast(tokens.solidHover, tokens.contrast) >= 4.5, `${color} ${mode} hover`);
      assert.ok(contrast(tokens.solidHover, tokens.contrast) > contrast(tokens.solid, tokens.contrast));
      const backgrounds = mode === 'light'
        ? ['#f5f6f8', '#ffffff', '#f8f9fb', '#f0f4f8', tokens.bg]
        : ['#171b21', '#20262e', '#1b2129', '#29313b', tokens.bg];
      for (const background of backgrounds) {
        assert.ok(contrast(tokens.text, background) >= 4.5, `${color} ${mode} text on ${background}`);
        assert.ok(contrast(tokens.textHover, background) >= 4.5, `${color} ${mode} hovered text on ${background}`);
        assert.ok(contrast(tokens.focusRing, background) >= 3, `${color} ${mode} focus on ${background}`);
      }
    }
    assert.notEqual(palette.light.text, palette.dark.text);
  }
});

test('host and hosted page views share a low-specificity theme focus ring', () => {
  for (const file of ['../../src/style.css', '../../src/features/plugins/page/PluginPageUi.css']) {
    const css = readFileSync(new URL(file, import.meta.url), 'utf8');
    assert.match(css, /:where\(:focus-visible\)\s*\{\s*outline: 2px solid var\(--accent-focus-ring\); outline-offset: 2px;/);
  }
});

test('theme choices persist and all semantic tokens follow explicit and system modes', () => {
  const f = fixture({ 'winpilot.theme': 'system', 'winpilot.accent': 'violet' }, true);
  f.subject.initializeTheme();
  f.subject.initializeTheme();
  assert.equal(f.listeners.length, 1);
  const theme = f.subject.useTheme();
  const check = (color, mode) => {
    for (const [token, property] of Object.entries(properties)) {
      assert.equal(f.values.get(property), getAccentTokens(color, mode)[token]);
    }
    assert.equal(f.values.size, 8);
  };
  check('violet', 'dark');
  for (const color of Object.keys(accentPalettes)) {
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
    assert.equal(f.values.get('--accent-solid'), accentPalettes.blue.light.solid);
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

test('secret fields share one focus ring across the input and visibility button', () => {
  const css = readFileSync(new URL('../../src/style.css', import.meta.url), 'utf8');
  assert.match(css, /\.secret-input\s*\{[^}]*border-radius:\s*var\(--radius-sm\)/);
  assert.match(css, /\.secret-input:focus-within\s*\{\s*box-shadow:\s*0 0 0 2px var\(--accent-focus-ring\)/);
  assert.match(css, /\.secret-input:focus-within input,\s*\.secret-input:focus-within \.secret-toggle\s*\{[^}]*outline:\s*none;\s*box-shadow:\s*none;/);
  assert.ok(!css.includes('.plugin-secret-input > .text-input:focus'));
});

test('status text and backgrounds stay readable in both explicit color modes', () => {
  const css = readFileSync(new URL('../../src/style.css', import.meta.url), 'utf8');
  const taskWarning = css.match(/\.task-warning\s*\{([^}]+)\}/)[1];
  assert.match(taskWarning, /color:\s*var\(--warning-text\);/);
  assert.ok(!taskWarning.includes('color-mix'));
  for (const mode of ['light', 'dark']) {
    const block = css.match(new RegExp(`\\[data-theme="${mode}"\\] \\{([^}]+)\\}`))[1];
    const values = Object.fromEntries([...block.matchAll(/(--[a-z-]+):\s*(#[a-f0-9]+)/g)].map(match => [match[1], match[2]]));
    for (const state of ['success', 'warning', 'danger']) {
      const text = values[`--${state}-text`];
      const background = values[`--${state}-bg`];
      assert.ok(text && background, `${mode} ${state} definitions`);
      for (const surface of [background, values['--canvas'], values['--surface'], values['--surface-hover']]) {
        assert.ok(contrast(text, surface) >= 4.5, `${mode} ${state} on ${surface}`);
      }
    }
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
  const defined = new Set(Object.values(properties));
  for (const [file, source] of sources) {
    for (const match of source.matchAll(/(--[a-z][a-z0-9-]*)\s*:/g)) defined.add(match[1]);
  }
  for (const [file, source] of sources) {
    for (const match of source.matchAll(/var\((--[a-z0-9-]+)\)/g)) {
      assert.ok(defined.has(match[1]), `${file.pathname}: ${match[1]}`);
    }
  }
});
