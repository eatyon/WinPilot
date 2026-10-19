import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';

const component = new URL('../../src/features/tasks/TaskVariableHelp.vue', import.meta.url);

function fixture(connected = true, fail = false) {
  let selected = false;
  let removed = false;
  let restored = false;
  class Element {}
  const previous = new Element();
  previous.focus = () => { restored = true; };
  const container = { isConnected: connected, append(input) { input.parent = this; } };
  const input = {
    style: {}, setAttribute() {},
    select() { assert.equal(this.parent, container); selected = true; },
    remove() { removed = true; },
  };
  const context = {
    exports: {}, HTMLElement: Element,
    defineProps: () => ({}), defineEmits: () => () => {}, require: () => ({}),
    document: {
      activeElement: previous,
      createElement: () => input,
      execCommand(command) {
        assert.equal(command, 'copy');
        assert.equal(selected, true);
        assert.equal(input.value, '$time');
        if (fail) throw new Error('copy failed');
        return true;
      },
    },
  };
  runInNewContext(loadScriptModule(component, 'copyTextLegacy'), context);
  return { copy: () => context.exports.copyTextLegacy('$time', container), state: () => ({ selected, removed, restored }) };
}

test('legacy variable copy selects inside the dialog and cleans up', () => {
  const f = fixture();
  assert.equal(f.copy(), true);
  assert.deepEqual(f.state(), { selected: true, removed: true, restored: true });
});

test('legacy variable copy cleans up after a failure', () => {
  const f = fixture(true, true);
  assert.equal(f.copy(), false);
  assert.deepEqual(f.state(), { selected: true, removed: true, restored: true });
});

test('legacy variable copy does not append into a closed dialog', () => {
  const f = fixture(false);
  assert.equal(f.copy(), false);
  assert.equal(f.state().selected, false);
});

test('variable help uses the system clipboard without invoking the fallback', async () => {
  const copied = [], toasts = [];
  class Element {
    closest(selector) {
      assert.equal(selector, '[role="dialog"]');
      return this;
    }
  }
  const context = {
    exports: {}, HTMLElement: Element,
    defineProps: () => ({}), defineEmits: () => () => {},
    require: name => name.endsWith('/toast') ? { showToast: (...args) => toasts.push(args) } : {},
    window: { isSecureContext: true },
    navigator: { clipboard: { writeText: async text => copied.push(text) } },
    document: { createElement: () => { throw new Error('unexpected fallback'); } },
  };
  runInNewContext(loadScriptModule(component, 'copyVariable'), context);
  await context.exports.copyVariable('$time', { currentTarget: new Element() });
  assert.deepEqual(copied, ['$time']);
  assert.deepEqual(toasts, [['变量已复制', 'success']]);
  await context.exports.copyVariable('$ignored', { currentTarget: null });
  assert.deepEqual(copied, ['$time']);
});
