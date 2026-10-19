import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { reactive, computed, ref } from 'vue';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as keyboardLayout from '../../src/features/desktop/keyboardLayout.ts';

const component = new URL('../../src/features/desktop/KeyboardKeyPicker.vue', import.meta.url);
const componentSource = readFileSync(component, 'utf8');
const script = loadScriptModule(component, 'isKeyDisabled,toggle,expanded,selected,toggleExpanded');

function fixture(keys = [], collapsible = false) {
  const props = reactive({ modelValue: [...keys], disabled: false, collapsible });
  const events = [];
  const context = {
    exports: {},
    require: name => name === 'vue' ? { computed, ref } : name.endsWith('/keyboardLayout') ? keyboardLayout : {},
    defineProps: () => props,
    withDefaults: value => value,
    defineEmits: () => (event, value) => {
      events.push(event);
      if (event === 'update:modelValue') props.modelValue = value;
    },
  };
  runInNewContext(script, context);
  return { props, events, ...context.exports };
}

test('collapsible pickers start closed and expanding does not alter selected keys', () => {
  const f = fixture(['A', 'B'], true);
  const other = fixture([], true);
  assert.equal(f.expanded.value, false);
  f.toggleExpanded();
  assert.equal(f.expanded.value, true);
  assert.equal(other.expanded.value, false);
  f.toggleExpanded();
  assert.equal(f.expanded.value, false);
  assert.deepEqual([...f.props.modelValue], ['A', 'B']);
  assert.deepEqual(f.events, []);
  f.toggle('A');
  assert.deepEqual([...f.props.modelValue], ['B']);
  assert.equal(f.expanded.value, false);
});

test('only task pickers opt into collapsing while the desktop keeps its keyboard visible', () => {
  const source = componentSource;
  const tasks = readFileSync(new URL('../../src/features/tasks/TaskActionEditor.vue', import.meta.url), 'utf8');
  const desktop = readFileSync(new URL('../../src/features/desktop/DesktopView.vue', import.meta.url), 'utf8');
  assert.match(source, /collapsible: false/);
  assert.match(source, /v-if="!collapsible \|\| expanded" class="physical-keyboard"/);
  assert.match(source, /v-if="collapsible"[\s\S]*?:aria-expanded="expanded"/);
  assert.match(tasks, /<KeyboardKeyPicker[^>]*\scollapsible\s*\/>/);
  assert.doesNotMatch(desktop, /<KeyboardKeyPicker[^>]*\bcollapsible\b/);
});

test('collapse controls reuse the picker arrow without a tooltip or visible text', () => {
  const source = componentSource;
  assert.doesNotMatch(source, /:title=/);
  assert.match(source, /:aria-label="expanded \? '收起键盘' : '展开键盘'"/);
  assert.ok(source.includes('<span class="app-select-chevron" aria-hidden="true" />'));
  assert.ok(!source.includes("{{ expanded ? '收起' : '展开' }}"));
});

test('the full-bar toggle expands directly while selected keys only remove themselves', () => {
  const f = fixture(['A'], true);
  f.toggleExpanded();
  assert.equal(f.expanded.value, true);
  assert.deepEqual([...f.props.modelValue], ['A']);
  assert.deepEqual(f.events, []);
  f.toggle('A');
  assert.equal(f.expanded.value, true);
  assert.deepEqual([...f.props.modelValue], []);
  f.toggleExpanded();
  assert.equal(f.expanded.value, false);
  f.props.disabled = true;
  f.toggleExpanded();
  assert.equal(f.expanded.value, false);
  const desktop = fixture();
  desktop.toggleExpanded();
  assert.equal(desktop.expanded.value, false);
});

test('the toggle covers the selection bar while selected key buttons remain independently clickable', () => {
  const styles = readFileSync(new URL('../../src/style.css', import.meta.url), 'utf8');
  assert.match(styles, /\.keyboard-expand-toggle\s*\{[^}]*position: absolute;[^}]*inset: 0;/);
  assert.match(styles, /\.selected-keys\.is-collapsible \.selected-key-values\s*\{[^}]*pointer-events: none;/);
  assert.match(styles, /\.selected-keys\.is-collapsible \.selected-key\s*\{[^}]*pointer-events: auto;/);
});

test('three selected keys disable only unselected keys and removing one restores selection', () => {
  const f = fixture(['Ctrl', 'Shift']);
  assert.equal(f.isKeyDisabled('A'), false);
  f.toggle('A');
  assert.equal(f.props.modelValue.length, 3);
  assert.equal(f.isKeyDisabled('B'), true);
  for (const key of f.props.modelValue) assert.equal(f.isKeyDisabled(key), false);
  f.toggle('Shift');
  assert.equal(f.props.modelValue.length, 2);
  assert.equal(f.isKeyDisabled('B'), false);
  f.toggle('B');
  assert.equal(f.isKeyDisabled('Shift'), true);
});

test('key ordering uses fixed modifier priority without mutating input or reordering normal keys', () => {
  for (const [keys, expected] of [
    [['A', 'Ctrl', 'B'], ['Ctrl', 'A', 'B']],
    [['B', 'A', 'Shift'], ['Shift', 'B', 'A']],
    [['A', 'Win', 'Alt'], ['Win', 'Alt', 'A']],
    [['B', 'A', 'C'], ['B', 'A', 'C']],
    [['Shift', 'Alt', 'Ctrl'], ['Ctrl', 'Alt', 'Shift']],
    [['Shift', 'Ctrl', 'Win'], ['Win', 'Ctrl', 'Shift']],
    [['A', 'Shift', 'Alt'], ['Alt', 'Shift', 'A']],
  ]) {
    const original = [...keys];
    assert.deepEqual(keyboardLayout.orderKeys(keys), expected);
    assert.deepEqual(keys, original);
  }
});

test('key selection prioritizes modifiers and preserves normal key order', () => {
  const f = fixture();
  f.toggle('B'); f.toggle('Ctrl'); f.toggle('A');
  assert.deepEqual([...f.props.modelValue], ['Ctrl', 'B', 'A']);
  f.toggle('Ctrl'); f.toggle('Ctrl');
  assert.deepEqual([...f.props.modelValue], ['Ctrl', 'B', 'A']);
  assert.doesNotMatch(componentSource, /→/);
});

test('all picker instances display fixed modifier order without mutating their incoming array', () => {
  const f = fixture(['Shift', 'Ctrl', 'Win']);
  assert.deepEqual([...f.selected.value], ['Win', 'Ctrl', 'Shift']);
  assert.deepEqual([...f.props.modelValue], ['Shift', 'Ctrl', 'Win']);
  f.toggle('Shift'); f.toggle('Alt');
  assert.deepEqual([...f.props.modelValue], ['Win', 'Ctrl', 'Alt']);
});

test('keyboard triggers use the same picker help and typography as keyboard actions', () => {
  const tasks = readFileSync(new URL('../../src/features/tasks/TasksView.vue', import.meta.url), 'utf8');
  const styles = readFileSync(new URL('../../src/style.css', import.meta.url), 'utf8');
  assert.match(tasks, /class="task-trigger-keyboard" data-task-field="keys"/);
  const picker = tasks.match(/<KeyboardKeyPicker[^>]*v-model="editor.trigger.keys"[^>]*\/>/)?.[0];
  assert.ok(picker);
  assert.doesNotMatch(picker, /\bhelp=/);
  assert.match(styles, /\.task-trigger-process, \.task-trigger-extension, \.task-trigger-keyboard\s*\{\s*display: grid; gap: 12px; margin-top: 12px;/);
});

test('desktop and task feature names consistently use key monitoring and simulation', () => {
  const desktop = readFileSync(new URL('../../src/features/desktop/DesktopView.vue', import.meta.url), 'utf8');
  const tasks = readFileSync(new URL('../../src/features/tasks/TasksView.vue', import.meta.url), 'utf8');
  const models = readFileSync(new URL('../../src/features/tasks/taskModels.ts', import.meta.url), 'utf8');
  assert.match(desktop, /启用按键模拟/);
  assert.match(models, /\['Keyboard', '按键模拟'\]/);
  assert.match(tasks, /value: 'Keyboard', label: '按键监听'/);
  assert.match(tasks, /按键模拟 ·/);
  assert.doesNotMatch(desktop + tasks + models, /键盘(?:模拟|监听)/);
});

test('global disabling still prevents removing selected keys', () => {
  const f = fixture(['A']);
  f.props.disabled = true;
  assert.equal(f.isKeyDisabled('A'), true);
  assert.equal(f.isKeyDisabled('B'), true);
  f.toggle('A');
  assert.equal(f.props.modelValue.length, 1);
  assert.deepEqual(f.events, []);
  f.props.disabled = false;
  assert.equal(f.isKeyDisabled('A'), false);
  assert.equal(f.isKeyDisabled('B'), false);
});

test('direct overflow attempts retain the existing three-key guard and limit event', () => {
  const f = fixture(['A', 'B', 'C']);
  f.toggle('D');
  assert.equal(f.props.modelValue.length, 3);
  assert.equal(f.props.modelValue.includes('D'), false);
  assert.deepEqual(f.events, ['limit']);
});

test('all four physical keyboard groups use the same per-key disabled condition', () => {
  const source = componentSource;
  assert.equal([...source.matchAll(/:disabled="isKeyDisabled\(key\)"/g)].length, 4);
  assert.match(source, /class="selected-key" :disabled="disabled"/);
});
