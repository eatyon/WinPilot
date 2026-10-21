import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { readFileSync } from 'node:fs';
import * as vue from 'vue';
import { loadScriptModule } from '../support/scriptModule.mjs';

function fixture(href, viewportWidth = 360, innerWidth = viewportWidth) {
  const listeners = new Map();
  let unmount;
  const context = {
    exports: {},
    defineProps: () => ({ label: '说明', href }),
    withDefaults: props => props,
    window: { innerWidth },
    document: {
      documentElement: { clientWidth: viewportWidth },
      addEventListener: (name, handler) => listeners.set(name, handler),
      removeEventListener: (name, handler) => {
        if (listeners.get(name) === handler) listeners.delete(name);
      },
    },
    require: () => ({ ...vue, useId: () => 'hint', onMounted: callback => callback(), onBeforeUnmount: callback => { unmount = callback; } }),
  };
  runInNewContext(loadScriptModule(new URL('../../src/components/HelpHint.vue', import.meta.url), 'root,button,open,left,toggle'), context);
  return { subject: context.exports, listeners, dispose: () => unmount() };
}

test('help hints toggle, dismiss outside or with Escape, fit the viewport and release listeners', () => {
  const f = fixture();
  try {
    const inside = {};
    let focused = true;
    f.subject.root.value = { getBoundingClientRect: () => ({ left: 200 }), contains: target => target === inside };
    f.subject.button.value = { blur: () => { focused = false; } };
    f.subject.toggle();
    assert.equal(f.subject.open.value, true);
    const tooltipLeft = 200 + f.subject.left.value;
    assert.ok(tooltipLeft >= 16 && tooltipLeft + 304 <= 344);
    f.listeners.get('pointerdown')({ target: inside });
    assert.equal(f.subject.open.value, true);
    f.subject.toggle();
    assert.equal(f.subject.open.value, false);
    f.subject.toggle();
    f.listeners.get('pointerdown')({ target: {} });
    assert.equal(f.subject.open.value, false);
    f.subject.toggle();
    focused = true;
    f.listeners.get('keydown')({ key: 'Escape' });
    assert.equal(f.subject.open.value, false);
    assert.equal(focused, false);
  } finally { f.dispose(); }
  assert.equal(f.listeners.size, 0);
});

test('closed help popovers have no layout box while focus and hover still reveal them', () => {
  const source = readFileSync(new URL('../../src/components/HelpHint.vue', import.meta.url), 'utf8');
  const rule = source.match(/\.help-hint-popover\s*\{([^}]+)\}/)?.[1];
  assert.match(rule, /display:\s*none/);
  assert.match(source, /\.help-hint-wrap:focus-within[^{}]+\{\s*display:\s*block/);
  assert.match(source, /\.help-hint-wrap:hover[^{}]+\{\s*display:\s*block/);
});

test('popover positioning uses the page width even when overflow inflates innerWidth', () => {
  for (const width of [320, 360, 390, 430, 768, 1265]) {
    const f = fixture(undefined, width, width + 15);
    try {
      const anchorLeft = width - 20;
      f.subject.root.value = { getBoundingClientRect: () => ({ left: anchorLeft }) };
      f.subject.toggle();
      const left = anchorLeft + f.subject.left.value;
      assert.ok(left >= 16);
      assert.ok(left + Math.min(320, width - 56) <= width - 16, `viewport ${width}`);
    } finally { f.dispose(); }
  }
});

test('linked help hints do not register popover listeners', () => {
  const f = fixture('https://cron.ciding.cc/');
  try { assert.equal(f.listeners.size, 0); }
  finally { f.dispose(); }
});
