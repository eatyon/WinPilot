import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { readFileSync } from 'node:fs';
import * as vue from 'vue';
import { loadScriptModule } from '../support/scriptModule.mjs';

test('single selects use null for empty prompts without changing stored values or real empty options', () => {
  const props = vue.reactive({ modelValue: '', options: [{ value: 'chosen', label: '已选择' }] });
  const context = {
    exports: {}, defineProps: () => props, withDefaults: value => value, defineEmits: () => () => {},
    require: name => name === 'vue' ? vue : { useSelectOpening: () => ({}) },
  };
  runInNewContext(loadScriptModule(new URL('../../src/components/AppSelect.vue', import.meta.url), 'selectedValue'), context);
  const selected = context.exports.selectedValue;
  assert.equal(selected.value, null);
  assert.equal(props.modelValue, '');
  props.options = [{ value: '', label: '禁用' }];
  assert.equal(selected.value, '');
  props.options = [{ value: 0, label: '零' }];
  props.modelValue = '0';
  assert.equal(selected.value, 0);
  props.modelValue = 'missing';
  assert.equal(selected.value, 'missing');
  props.modelValue = null;
  assert.equal(selected.value, null);
});

test('host task and plugin forms use placeholders instead of synthetic empty prompt options', () => {
  for (const file of ['features/tasks/TaskActionEditor.vue', 'features/tasks/TasksView.vue', 'features/tasks/taskModels.ts', 'features/plugins/PluginSettingField.vue']) {
    const source = readFileSync(new URL(`../../src/${file}`, import.meta.url), 'utf8');
    assert.doesNotMatch(source, /\{\s*value:\s*'',\s*label:[^\n]*(?:请选择|暂无可用)/, file);
  }
  const security = readFileSync(new URL('../../src/features/settings/tabs/SecuritySettings.vue', import.meta.url), 'utf8');
  assert.match(security, /\{ value: '', label: '禁用' \}/);
});

test('notification channel clearing uses the library control and preserves empty-string storage', () => {
  const emitted = [];
  const props = vue.reactive({ modelValue: 'windows', options: [{ value: 'windows', label: 'Windows 通知' }] });
  const context = {
    exports: {}, defineProps: () => props, withDefaults: value => value,
    defineEmits: () => (event, value) => { emitted.push([event, value]); if (event === 'update:modelValue') props.modelValue = value; },
    require: name => name === 'vue' ? vue : { useSelectOpening: () => ({}) },
  };
  runInNewContext(loadScriptModule(new URL('../../src/components/AppSelect.vue', import.meta.url), 'choose, selectedValue'), context);
  context.exports.choose(null);
  assert.deepEqual(emitted, [['update:modelValue', ''], ['change', '']]);
  assert.equal(props.modelValue, '');
  assert.equal(context.exports.selectedValue.value, null);
  context.exports.choose(0);
  assert.equal(props.modelValue, 0);
  const select = readFileSync(new URL('../../src/components/AppSelect.vue', import.meta.url), 'utf8');
  const field = readFileSync(new URL('../../src/features/plugins/PluginSettingField.vue', import.meta.url), 'utf8');
  assert.match(select, /clearable: false/);
  assert.match(select, /:clearable="clearable"/);
  assert.match(field, /:clearable="field.optionsSource === 'notificationChannels'"/);
});

function fixture(beforeOpen) {
  const disabled = vue.ref(false);
  const toasts = [];
  const context = { exports: {}, require: name => name === 'vue' ? vue : { showToast: message => toasts.push(message) } };
  runInNewContext(loadScriptModule(new URL('../../src/ui/useSelectOpening.ts', import.meta.url)), context);
  const scope = vue.effectScope();
  const subject = scope.run(() => context.exports.useSelectOpening(() => disabled.value, () => beforeOpen));
  return { subject, disabled, toasts, dispose: () => scope.stop() };
}

test('ordinary selects open without an unnecessary loading state', async () => {
  const f = fixture();
  try {
    await f.subject.updateShow(true);
    assert.equal(f.subject.open.value, true);
    assert.equal(f.subject.loading.value, false);
  } finally { f.dispose(); }
});

test('select opening waits for options and ignores repeated activation', async () => {
  let resolve, calls = 0;
  const f = fixture(() => { calls++; return new Promise(done => { resolve = done; }); });
  try {
    const pending = f.subject.updateShow(true);
    await f.subject.updateShow(true);
    assert.equal(calls, 1);
    assert.equal(f.subject.loading.value, true);
    assert.equal(f.subject.open.value, false);
    resolve();
    await pending;
    assert.equal(f.subject.open.value, true);
    assert.equal(f.subject.loading.value, false);
  } finally { f.dispose(); }
});

test('closed, disabled and disposed selects do not reopen after delayed loading', async () => {
  for (const action of ['close', 'disable', 'dispose']) {
    let resolve;
    const f = fixture(() => new Promise(done => { resolve = done; }));
    const pending = f.subject.updateShow(true);
    if (action === 'close') await f.subject.updateShow(false);
    else if (action === 'disable') f.disabled.value = true;
    else f.dispose();
    resolve();
    await pending;
    assert.equal(f.subject.open.value, false, action);
    assert.equal(f.subject.loading.value, false, action);
    if (action !== 'dispose') f.dispose();
  }
});

test('option loading may temporarily disable its owner without cancelling the opening', async () => {
  let resolve;
  const f = fixture(async () => {
    f.disabled.value = true;
    await new Promise(done => { resolve = done; });
    f.disabled.value = false;
  });
  try {
    const pending = f.subject.updateShow(true);
    resolve();
    await pending;
    assert.equal(f.subject.open.value, true);
  } finally { f.dispose(); }
});

test('rejected opening and loading errors leave controls usable', async () => {
  for (const fail of [false, true]) {
    const f = fixture(async () => { if (fail) throw new Error('demo'); return false; });
    try {
      await f.subject.updateShow(true);
      assert.equal(f.subject.open.value, false);
      assert.equal(f.subject.loading.value, false);
      assert.equal(f.toasts.length, fail ? 1 : 0);
    } finally { f.dispose(); }
  }
});

test('multi-select emits only changed editable membership', () => {
  const emitted = [];
  const props = { modelValue: ['old', 'locked'], options: [{ value: 'old' }, { value: 'new' }, { value: 'locked', disabled: true }] };
  const context = {
    exports: {}, defineProps: () => props, withDefaults: value => value,
    defineEmits: () => (...args) => emitted.push(args),
    require: () => ({ useSelectOpening: () => ({}) }),
  };
  runInNewContext(loadScriptModule(new URL('../../src/components/AppMultiSelect.vue', import.meta.url), 'choose'), context);
  context.exports.choose(['new']);
  assert.deepEqual(emitted, [['toggle', 'old', false], ['toggle', 'new', true]]);
});
