import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import * as vue from 'vue';
import { loadScriptModule } from '../support/scriptModule.mjs';

function fixture(href) {
  const listeners = new Map();
  let unmount;
  const context = {
    exports: {},
    defineProps: () => ({ label: '说明', href }),
    withDefaults: props => props,
    window: { innerWidth: 360 },
    document: {
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

test('linked help hints do not register popover listeners', () => {
  const f = fixture('https://cron.ciding.cc/');
  try { assert.equal(f.listeners.size, 0); }
  finally { f.dispose(); }
});
