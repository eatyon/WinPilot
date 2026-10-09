import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

function fixture(connected = true, fail = false) {
  const source = readFileSync(new URL('../../src/features/tasks/TasksView.vue', import.meta.url), 'utf8');
  const helper = source.slice(source.indexOf('function copyTextLegacy('), source.indexOf('async function copyVariable('));
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
  runInNewContext(ts.transpileModule(helper + '\nexports.copy = copyTextLegacy;', {}).outputText, context);
  return { copy: () => context.exports.copy('$time', container), state: () => ({ selected, removed, restored }) };
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
