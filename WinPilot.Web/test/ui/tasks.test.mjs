import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { readFileSync } from 'node:fs';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';
import * as apiErrors from '../support/apiErrors.mjs';
import { combineRequestSignal } from '../../src/api/requestSignal.ts';
import * as taskModels from '../../src/features/tasks/taskModels.ts';

const taskValidationModule = { exports: {}, require: () => taskModels };
runInNewContext(loadScriptModule(new URL('../../src/features/tasks/taskValidation.ts', import.meta.url)), taskValidationModule);

function fixture({ reducedMotion = false } = {}) {
  const code = loadScriptModule(new URL('../../src/features/tasks/TasksView.vue', import.meta.url),
    'tasks, tasksLoaded, tags, registeredActions, registeredTriggers, pageError, detailTarget, editor, editorForm, focusTaskError, invalidActionField, invalidTriggerField, invalidProcessPath, fieldErrors, actionErrors, move, actionKey, toggleMultiOption, deleteTarget, runTarget, triggerLabel, extensionActionLabel, extensionTriggerLabel, loadActionOptions, detailActionOptions, taskWarningText, loadTasks, saveTask, deleteTask, toggleTaskEnabled, requestRun, runTask, selectTriggerParameter, parameterDefaults, pendingOperations, createAction, validate, selectActionType, openTaskEditor, actionPayload, channelOptions, requestControllers');
  const requests = [];
  const toasts = [];
  let unmount;
  const focusModule = { exports: {}, window: { matchMedia: () => ({ matches: reducedMotion }) } };
  runInNewContext(loadScriptModule(new URL('../../src/ui/focusInvalidInput.ts', import.meta.url)), focusModule);
  const client = { ...apiErrors, apiFetch: (url, init) => new Promise(resolve => requests.push({ url, init, resolve })), isAbortError: error => error?.name === 'AbortError', userErrorMessage: error => error.message, WinPilotConnectionError: class extends Error {} };
  const executor = { exports: {}, AbortController, DOMException, require: name => name.endsWith('/client') ? client : { combineRequestSignal } };
  runInNewContext(loadScriptModule(new URL('../../src/api/requestExecutor.ts', import.meta.url)), executor);
  const context = {
    exports: {}, AbortController, DOMException, HTMLInputElement: class {},
    require: name => {
      if (name === 'vue') return { ...vue, onMounted: () => {}, onBeforeUnmount: callback => { unmount = callback; } };
      if (name.endsWith('/client')) return client;
      if (name.endsWith('/requestExecutor')) return executor.exports;
      if (name.endsWith('/toast')) return { showToast: text => toasts.push(text) };
      if (name.endsWith('/focusInvalidInput')) return focusModule.exports;
      if (name.endsWith('/safeMode')) return { safeModeEnabled: vue.ref(false) };
      if (name === './taskModels') return taskModels;
      if (name === './taskValidation') return taskValidationModule.exports;
      return {};
    },
  };
  const scope = vue.effectScope();
  scope.run(() => runInNewContext(code, context));
  const subject = context.exports;
  subject.registeredActions.value = [{ pluginId: 'plugin', actionId: 'action', actionName: '运行任务', available: true, dynamicOptions: true, parameters: [{ key: 'name', type: 'Select', options: [] }] }];
  const action = { type: 'Extension', pluginId: 'plugin', actionId: 'action', parameters: { name: 'saved' } };
  const open = () => { subject.detailTarget.value = { actions: [action, { ...action }] }; };
  const reply = async (options, ok = true) => {
    requests.at(-1).resolve({ ok, json: async () => options });
    await setImmediate();
  };
  return { subject, requests, toasts, action, open, reply, dispose: () => { unmount(); scope.stop(); } };
}

test('action render identities follow reordering without altering selected keys or persistence', () => {
  const f = fixture();
  try {
    f.subject.openTaskEditor();
    const actions = f.subject.editor.value.actions;
    actions[0].type = 'Keyboard';
    actions[0].keys = ['A'];
    actions.push({ ...f.subject.createAction(), type: 'Keyboard', keys: ['B', 'C'] });
    const first = actions[0];
    const second = actions[1];
    const firstKey = f.subject.actionKey(first);
    const secondKey = f.subject.actionKey(second);
    assert.notEqual(firstKey, secondKey);
    assert.equal(f.subject.actionKey(vue.toRaw(first)), firstKey);
    f.subject.move(actions, 0, -1);
    assert.equal(f.subject.actionKey(actions[0]), firstKey);
    f.subject.move(actions, 0, 1);
    assert.equal(actions[0], second);
    assert.equal(actions[1], first);
    assert.equal(f.subject.actionKey(actions[0]), secondKey);
    assert.equal(f.subject.actionKey(actions[1]), firstKey);
    assert.deepEqual([...first.keys], ['A']);
    assert.deepEqual([...second.keys], ['B', 'C']);
    f.subject.move(actions, 1, -1);
    assert.equal(actions[0], first);
    assert.equal(f.subject.actionKey(actions[0]), firstKey);
    assert.notEqual(f.subject.actionKey({ ...first }), firstKey);
    assert.deepEqual(JSON.parse(JSON.stringify(f.subject.actionPayload(first))), { type: 'Keyboard', keys: ['A'] });
    assert.equal(f.requests.length, 0);
    const source = readFileSync(new URL('../../src/features/tasks/TasksView.vue', import.meta.url), 'utf8');
    assert.match(source, /<article v-for="\(action, index\) in editor.actions" :key="actionKey\(action\)"/);
    assert.doesNotMatch(source, /keyboardOrderVersion/);
  } finally { f.dispose(); }
});


test('task state actions require explicit selections and submit only their own fields', async () => {
  const f = fixture();
  try {
    const task = openValidTaskEditor(f);
    f.subject.selectActionType(0, 'TaskState');
    const action = task.actions[0];
    assert.equal(action.taskName, '');
    assert.equal(action.state, '');
    await f.subject.saveTask();
    assert.equal(f.subject.actionErrors.get(action).message, '请选择任务');
    assert.equal(f.requests.length, 0);
    action.taskName = 'target';
    await vue.nextTick();
    assert.equal(f.subject.actionErrors.has(action), false);
    await f.subject.saveTask();
    assert.equal(f.subject.actionErrors.get(action).message, '请选择状态');
    for (const state of ['Toggle', 'Enable', 'Disable']) {
      action.state = state;
      await vue.nextTick();
      assert.equal(f.subject.actionErrors.has(action), false);
      const pending = f.subject.saveTask();
      const request = f.requests.at(-1);
      assert.deepEqual(JSON.parse(request.init.body).actions[0], { type: 'TaskState', taskName: 'target', state });
      request.resolve(new Response(JSON.stringify({ detail: '保存失败' }), { status: 500 }));
      await pending;
    }
  } finally { f.dispose(); }
});

test('unavailable Cron uses the existing warning and preview suffix without changing the saved expression or enabled state', () => {
  const f = fixture();
  try {
    const trigger = { ...taskModels.createTrigger(), cron: '0 0 0 31 2 *' };
    const task = { name: 'Unreachable', enabled: true, trigger, actions: [], cronAvailable: false };
    assert.equal(f.subject.taskWarningText(task), 'Cron 表达式没有后续执行时间');
    assert.equal(f.subject.triggerLabel(trigger, task.cronAvailable), 'Cron 计划 · 0 0 0 31 2 *（不可用）');
    f.subject.openTaskEditor(task);
    assert.equal(f.subject.editor.value.enabled, true);
    assert.equal(f.subject.editor.value.trigger.cron, '0 0 0 31 2 *');
    assert.equal('cronAvailable' in f.subject.editor.value, false);
    assert.equal(f.subject.taskWarningText({ ...task, cronAvailable: true }), '');
    assert.equal(f.subject.taskWarningText({ ...task, cronAvailable: undefined }), '');
    assert.equal(f.subject.triggerLabel(trigger), 'Cron 计划 · 0 0 0 31 2 *');
    assert.equal(f.subject.taskWarningText({ ...task, enabled: false }), 'Cron 表达式没有后续执行时间');
    const source = readFileSync(new URL('../../src/features/tasks/TasksView.vue', import.meta.url), 'utf8');
    assert.ok(source.includes('triggerLabel(detailTarget.trigger, detailTarget.cronAvailable)'));
    assert.ok(source.includes('class="task-warning"'));
  } finally { f.dispose(); }
});

test('process trigger preview describes the event without exposing configured paths', () => {
  const f = fixture();
  try {
    const trigger = { type: 'Process', processPaths: ['C:\\private\\app.exe'] };
    assert.equal(f.subject.triggerLabel({ ...trigger, event: 'Started' }), '程序事件 · 程序启动时');
    assert.equal(f.subject.triggerLabel({ ...trigger, event: 'Exited' }), '程序事件 · 程序退出时');
    assert.equal(f.subject.triggerLabel({ ...trigger, event: '' }), '程序事件 · 未选择事件');
  } finally {
    f.dispose();
  }
});

test('disabled tasks can be manually run without enabling automatic triggers', async () => {
  const f = fixture();
  try {
    const task = { name: 'disabled task', enabled: false };
    f.subject.requestRun(task);
    assert.equal(f.subject.runTarget.value.name, task.name);
    assert.equal(f.subject.runTarget.value.enabled, false);
    assert.equal(f.requests.length, 0);
    assert.deepEqual(f.toasts, []);
    const pending = f.subject.runTask();
    assert.equal(f.requests.length, 1);
    assert.equal(f.requests[0].url, '/api/tasks/disabled%20task/run');
    assert.equal(f.requests[0].init.method, 'POST');
    f.requests[0].resolve(new Response(null, { status: 204 }));
    await pending;
    assert.equal(f.subject.runTarget.value, undefined);
    assert.equal(task.enabled, false);
  } finally {
    f.dispose();
  }
});

test('name and cron edits clear only their own save errors and saving validates again', async () => {
  const f = fixture();
  try {
    f.subject.openTaskEditor();
    const task = f.subject.editor.value;
    task.trigger.type = 'Cron';
    task.trigger.cron = '';
    await f.subject.saveTask();
    assert.equal(f.subject.fieldErrors.value.name, '请输入任务名称');
    assert.equal(f.subject.fieldErrors.value.cron, '请输入 Cron 表达式');
    assert.equal(f.subject.actionErrors.size, 1);
    task.enabled = !task.enabled;
    await vue.nextTick();
    assert.equal(f.subject.fieldErrors.value.cron, '请输入 Cron 表达式');
    task.name = 'Task';
    assert.equal(f.subject.fieldErrors.value.name, undefined);
    assert.equal(f.subject.fieldErrors.value.cron, '请输入 Cron 表达式');
    task.trigger.cron = '0 30 8 * * *';
    assert.equal(f.subject.fieldErrors.value.cron, undefined);
    assert.equal(f.subject.actionErrors.size, 1);
    task.trigger.cron = '';
    await f.subject.saveTask();
    assert.equal(f.subject.fieldErrors.value.cron, '请输入 Cron 表达式');
    assert.equal(f.requests.length, 0);
  } finally { f.dispose(); }
});

test('process path edits additions removals and trigger type changes clear stale trigger errors', async () => {
  const f = fixture();
  try {
    f.subject.openTaskEditor();
    const trigger = f.subject.editor.value.trigger;
    trigger.type = 'Process';
    trigger.event = 'Started';
    trigger.processPaths = [];
    await f.subject.saveTask();
    assert.equal(f.subject.fieldErrors.value.processPaths, '请至少选择一个程序绝对路径');
    trigger.processPaths.push('app.exe');
    assert.equal(f.subject.fieldErrors.value.processPaths, undefined);
    await f.subject.saveTask();
    assert.equal(f.subject.fieldErrors.value.processPaths, '程序路径必须是绝对路径');
    trigger.processPaths[0] = 'C:\\app.exe';
    assert.equal(f.subject.fieldErrors.value.processPaths, undefined);
    trigger.processPaths[0] = 'relative';
    await f.subject.saveTask();
    trigger.processPaths.splice(0, 1);
    assert.equal(f.subject.fieldErrors.value.processPaths, undefined);
    await f.subject.saveTask();
    trigger.type = 'WinPilotStartup';
    assert.equal(f.subject.fieldErrors.value.processPaths, undefined);
    assert.equal(f.subject.fieldErrors.value.name, '请输入任务名称');
  } finally { f.dispose(); }
});

test('plugin trigger parameter edits clear trigger errors without clearing action errors', async () => {
  const f = fixture();
  try {
    f.subject.openTaskEditor();
    f.subject.registeredTriggers.value = [{ pluginId: 'plugin', triggerId: 'trigger', available: true,
      parameters: [{ key: 'target', label: '目标', type: 'Text', required: true }] }];
    Object.assign(f.subject.editor.value.trigger, { type: 'Extension', pluginId: 'plugin', triggerId: 'trigger', parameters: { target: '' } });
    await f.subject.saveTask();
    assert.equal(f.subject.fieldErrors.value.extension, '请输入目标');
    f.subject.editor.value.trigger.parameters.target = 'selected';
    assert.equal(f.subject.fieldErrors.value.extension, undefined);
    assert.equal(f.subject.actionErrors.size, 1);
    assert.equal(f.subject.fieldErrors.value.name, '请输入任务名称');
  } finally { f.dispose(); }
});

test('plugin action parameter edits including nested multi-select changes clear only that action error', async () => {
  const f = fixture();
  try {
    f.subject.openTaskEditor();
    f.subject.registeredActions.value = [{ pluginId: 'plugin', actionId: 'action', available: true,
      parameters: [{ key: 'targets', label: '目标', type: 'MultiCheckbox', required: true, options: [{ value: 'one', label: '一' }] }] }];
    const action = f.subject.editor.value.actions[0];
    Object.assign(action, { type: 'Extension', pluginId: 'plugin', actionId: 'action', parameters: { targets: [] } });
    await f.subject.saveTask();
    assert.equal(f.subject.actionErrors.get(action)?.message, '请选择目标');
    f.subject.toggleMultiOption(action, 'targets', 'one', true);
    await vue.nextTick();
    assert.equal(f.subject.actionErrors.has(action), false);
    f.subject.toggleMultiOption(action, 'targets', 'one', false);
    await f.subject.saveTask();
    await vue.nextTick();
    assert.equal(f.subject.actionErrors.get(action)?.message, '请选择目标');
    f.subject.selectActionType(0, 'Delay');
    await vue.nextTick();
    assert.equal(f.subject.actionErrors.has(action), false);
    assert.equal(f.subject.fieldErrors.value.name, '请输入任务名称');
  } finally { f.dispose(); }
});

test('action errors follow identity through reorder deletion and insertion and release with the editor', async () => {
  const f = fixture();
  try {
    f.subject.openTaskEditor();
    const task = f.subject.editor.value;
    task.actions.push({ ...f.subject.createAction(), type: 'Delay', delaySeconds: '' });
    const [program, delay] = task.actions;
    await f.subject.saveTask();
    f.subject.move(task.actions, 0, 1);
    await vue.nextTick();
    assert.equal(task.actions[0], delay);
    assert.equal(f.subject.actionErrors.get(task.actions[0])?.message, '请输入有效的延迟秒数');
    assert.equal(f.subject.actionErrors.get(task.actions[1])?.message, '请选择要启动的程序');
    task.actions.splice(0, 1);
    await vue.nextTick();
    assert.equal(f.subject.actionErrors.has(delay), false);
    assert.equal(f.subject.actionErrors.get(program)?.message, '请选择要启动的程序');
    task.actions.unshift(f.subject.createAction());
    await vue.nextTick();
    assert.equal(f.subject.actionErrors.has(task.actions[0]), false);
    assert.equal(f.subject.actionErrors.get(task.actions[1])?.message, '请选择要启动的程序');
    f.subject.editor.value = undefined;
    await vue.nextTick();
    assert.equal(f.subject.actionErrors.size, 0);
    f.subject.openTaskEditor();
    assert.equal(Object.keys(f.subject.fieldErrors.value).length, 0);
  } finally { f.dispose(); }
});

function openValidTaskEditor(f) {
  f.subject.openTaskEditor();
  const task = f.subject.editor.value;
  task.name = 'Task';
  task.trigger.cron = '0 30 8 * * *';
  task.actions[0].programPath = 'app.exe';
  return task;
}

function attachEditorForm(f, fields, actions = []) {
  const calls = [];
  const controls = names => names.map(name => ({
    dataset: { taskField: name }, matches: () => false,
    querySelector: () => ({
      closest: () => null,
      scrollIntoView: () => calls.push(`${name}:scroll`),
      focus: () => calls.push(`${name}:focus`),
      animate: () => calls.push(`${name}:animate`),
    }),
  }));
  const formFields = controls(fields);
  const actionFields = actions.map(controls);
  f.subject.editorForm.value = {
    querySelectorAll: () => formFields,
    querySelector: selector => {
      const index = Number(selector.match(/data-task-action="(\d+)"/)?.[1]);
      return actionFields[index] ? { querySelectorAll: () => actionFields[index] } : null;
    },
  };
  return calls;
}

test('save focuses and shakes only the first invalid task field in form order', async () => {
  const f = fixture();
  try {
    const task = openValidTaskEditor(f);
    task.name = '';
    task.trigger.cron = '';
    task.actions[0].programPath = '';
    const calls = attachEditorForm(f, ['name', 'cron'], [['programPath']]);
    await f.subject.saveTask();
    assert.deepEqual(calls, ['name:scroll', 'name:focus', 'name:animate']);
    assert.equal(f.requests.length, 0);
    calls.length = 0;
    task.name = 'Task';
    await f.subject.saveTask();
    assert.deepEqual(calls, ['cron:scroll', 'cron:focus', 'cron:animate']);
  } finally { f.dispose(); }
});

test('keyboard validation focuses the expand button but scrolls and shakes the whole selection bar', async () => {
  for (const reducedMotion of [false, true]) {
    const f = fixture({ reducedMotion });
    try {
      const task = openValidTaskEditor(f);
      task.actions[0].type = 'Keyboard';
      const calls = [];
      const bar = {
        scrollIntoView: () => calls.push('bar:scroll'),
        animate: () => calls.push('bar:animate'),
      };
      const button = {
        focus: () => calls.push('button:focus'),
        animate: () => calls.push('button:animate'),
        click: () => calls.push('button:click'),
        closest: selector => { assert.equal(selector, '.selected-keys'); return bar; },
      };
      const field = { dataset: { taskField: 'keys' }, matches: () => false, querySelector: () => button };
      f.subject.editorForm.value = {
        querySelectorAll: () => [],
        querySelector: () => ({ querySelectorAll: () => [field] }),
      };
      await f.subject.saveTask();
      assert.deepEqual(calls, reducedMotion ? ['bar:scroll', 'button:focus'] : ['bar:scroll', 'button:focus', 'bar:animate']);
      assert.equal(f.requests.length, 0);
    } finally { f.dispose(); }
  }
});

test('process validation focuses the event first, then the invalid path or add button', async () => {
  const f = fixture();
  try {
    const task = openValidTaskEditor(f);
    task.trigger.type = 'Process';
    task.trigger.event = '';
    task.trigger.processPaths = [];
    const calls = attachEditorForm(f, ['event', 'processPaths.0', 'processPaths.1', 'addProcess']);
    await f.subject.saveTask();
    assert.equal(calls[1], 'event:focus');
    calls.length = 0;
    task.trigger.event = 'Started';
    task.trigger.processPaths = ['C:\\app.exe', 'relative.exe'];
    await f.subject.saveTask();
    assert.equal(calls[1], 'processPaths.1:focus');
    calls.length = 0;
    task.trigger.processPaths = [];
    const emptyCalls = attachEditorForm(f, ['event', 'addProcess']);
    await f.subject.saveTask();
    assert.equal(emptyCalls[1], 'addProcess:focus');
  } finally { f.dispose(); }
});

test('plugin parameter errors focus the parameter found by the shared validation rules', async () => {
  for (const trigger of [false, true]) {
    const f = fixture();
    try {
      const task = openValidTaskEditor(f);
      const parameters = [{ key: 'target.with.dot', label: '目标', type: 'Text', required: true }];
      if (trigger) {
        f.subject.registeredTriggers.value = [{ pluginId: 'plugin', triggerId: 'trigger', parameters }];
        Object.assign(task.trigger, { type: 'Extension', pluginId: 'plugin', triggerId: 'trigger', parameters: {} });
      } else {
        f.subject.registeredActions.value = [{ pluginId: 'plugin', actionId: 'action', parameters }];
        Object.assign(task.actions[0], { type: 'Extension', pluginId: 'plugin', actionId: 'action', parameters: {} });
      }
      const field = 'parameters.target.with.dot';
      const calls = attachEditorForm(f, trigger ? ['pluginId', 'triggerId', field] : [], [['type', 'pluginId', 'actionId', field]]);
      await f.subject.saveTask();
      assert.deepEqual(calls, [`${field}:scroll`, `${field}:focus`, `${field}:animate`]);
    } finally { f.dispose(); }
  }
});

test('save validation respects reduced motion and does not focus a closed or replaced editor', async () => {
  for (const close of [false, true]) {
    const f = fixture({ reducedMotion: true });
    try {
      const task = openValidTaskEditor(f);
      task.name = '';
      const calls = attachEditorForm(f, ['name']);
      const pending = f.subject.saveTask();
      if (close) f.subject.openTaskEditor();
      await pending;
      assert.deepEqual(calls, close ? [] : ['name:scroll', 'name:focus']);
    } finally { f.dispose(); }
  }
});

test('structured server validation focuses the reported action field while server failures do not focus', async () => {
  for (const status of [400, 500]) {
    const f = fixture();
    try {
      openValidTaskEditor(f);
      const calls = attachEditorForm(f, [], [['type', 'programPath', 'workingDirectory']]);
      const pending = f.subject.saveTask();
      f.requests[0].resolve({ ok: false, status, json: async () => ({ errors: { 'actions[0].workingDirectory': ['目录无效'] } }) });
      await pending;
      assert.deepEqual(calls, status === 400 ? ['workingDirectory:scroll', 'workingDirectory:focus', 'workingDirectory:animate'] : []);
    } finally { f.dispose(); }
  }
});

test('server plugin parameter feedback focuses its exact declared control without relying on message text', async () => {
  const f = fixture();
  try {
    const task = openValidTaskEditor(f);
    Object.assign(task.trigger, { type: 'Extension', pluginId: 'plugin', triggerId: 'trigger', parameters: { count: 5 } });
    f.subject.registeredTriggers.value = [{ pluginId: 'plugin', triggerId: 'trigger', parameters: [{ key: 'count', type: 'Number' }] }];
    const calls = attachEditorForm(f, ['pluginId', 'triggerId', 'parameters.count']);
    const pending = f.subject.saveTask();
    f.requests[0].resolve({ ok: false, status: 400, json: async () => ({ errors: { 'trigger.parameters.count': ['无效值'] } }) });
    await pending;
    assert.deepEqual(calls, ['parameters.count:scroll', 'parameters.count:focus', 'parameters.count:animate']);
  } finally { f.dispose(); }
});

test('correcting a field or leaving the page before rendering validation prevents late focus', async () => {
  for (const leavePage of [false, true]) {
    const f = fixture();
    try {
      const task = openValidTaskEditor(f);
      task.name = '';
      const calls = attachEditorForm(f, ['name']);
      const pending = f.subject.saveTask();
      if (leavePage) f.dispose();
      else task.name = 'Corrected';
      await pending;
      assert.deepEqual(calls, []);
    } finally { f.dispose(); }
  }
});

test('invalid action borders use the same field as focus and clear when the action is edited', async () => {
  const f = fixture();
  try {
    const task = openValidTaskEditor(f);
    Object.assign(task.actions[0], { type: 'TaskState', taskName: '', state: '' });
    const action = task.actions[0];
    assert.equal(f.subject.invalidActionField(action), undefined);
    await f.subject.saveTask();
    assert.equal(f.subject.invalidActionField(action), 'taskName');
    action.taskName = 'Other';
    await vue.nextTick();
    assert.equal(f.subject.invalidActionField(action), undefined);
    await f.subject.saveTask();
    assert.equal(f.subject.invalidActionField(action), 'state');
    action.state = 'Enable';
    await vue.nextTick();
    assert.equal(f.subject.invalidActionField(action), undefined);
  } finally { f.dispose(); }
});

test('plugin and process invalid field markers clear together with their validation messages', async () => {
  const f = fixture();
  try {
    const task = openValidTaskEditor(f);
    Object.assign(task.trigger, { type: 'Extension', pluginId: '', triggerId: '' });
    await f.subject.saveTask();
    assert.equal(f.subject.invalidTriggerField.value, 'pluginId');
    task.trigger.pluginId = 'plugin';
    assert.equal(f.subject.invalidTriggerField.value, undefined);
    await f.subject.saveTask();
    assert.equal(f.subject.invalidTriggerField.value, 'triggerId');
    Object.assign(task.trigger, { type: 'Process', event: 'Started', processPaths: ['C:\\app.exe', 'relative.exe'] });
    await f.subject.saveTask();
    assert.equal(f.subject.invalidProcessPath.value, 1);
    assert.notEqual(f.subject.fieldErrors.value.processPaths, undefined);
    task.trigger.processPaths[1] = 'C:\\other.exe';
    assert.equal(f.subject.fieldErrors.value.processPaths, undefined);
  } finally { f.dispose(); }
});

test('server action field markers retain the reported field and clear after correction', async () => {
  const f = fixture();
  try {
    const task = openValidTaskEditor(f);
    const action = task.actions[0];
    const pending = f.subject.saveTask();
    f.requests[0].resolve({ ok: false, status: 400, json: async () => ({ errors: { 'actions[0].workingDirectory': ['目录无效'] } }) });
    await pending;
    assert.equal(f.subject.invalidActionField(action), 'workingDirectory');
    action.workingDirectory = 'C:\\app';
    await vue.nextTick();
    assert.equal(f.subject.invalidActionField(action), undefined);
  } finally { f.dispose(); }
});

test('structured save validation shows field feedback and a single summary toast', async () => {
  for (const [status, path, message, field] of [
    [400, 'name', '任务名称无效', 'name'],
    [409, 'name', '同名任务已存在', 'name'],
    [400, 'trigger.cron', 'Cron 表达式格式无效', 'cron'],
    [400, 'trigger.event', '请选择事件', 'event'],
    [400, 'actions[0].programPath', '程序路径无效', 'action'],
    [400, 'actions[0].workingDirectory', '起始目录不存在', 'action'],
  ]) {
    const f = fixture();
    try {
      const task = openValidTaskEditor(f);
      if (field === 'event') {
        task.trigger.type = 'Process';
        task.trigger.event = 'Started';
        task.trigger.processPaths = ['C:\\app.exe'];
      }
      const pending = f.subject.saveTask();
      f.requests[0].resolve({ ok: false, status, json: async () => ({ detail: message, errors: { [path]: [message] } }) });
      await pending;
      assert.equal(field === 'action' ? f.subject.actionErrors.get(task.actions[0])?.message : f.subject.fieldErrors.value[field], message);
      if (field === 'action') assert.equal(f.subject.actionErrors.get(task.actions[0])?.field, path.replace(/^actions\[0\]\./, ''));
      assert.deepEqual(f.toasts, ['请检查任务表单中的配置']);
      assert.equal(f.subject.editor.value, task);
      assert.equal(f.subject.pendingOperations.size, 0);
      if (field === 'action') task.actions[0].programPath = 'other.exe';
      else if (field === 'cron') task.trigger.cron = '0 0 10 * * *';
      else if (field === 'event') task.trigger.event = 'Exited';
      else task.name = 'New';
      await vue.nextTick();
      assert.equal(field === 'action' ? f.subject.actionErrors.has(task.actions[0]) : field in f.subject.fieldErrors.value, false);
    } finally { f.dispose(); }
  }
});

test('repeated server validation keeps feedback visible until the replacement response is read', async () => {
  for (const field of ['programPath', 'workingDirectory', 'notificationContent']) {
    const f = fixture();
    let stop;
    try {
      const task = openValidTaskEditor(f);
      const action = task.actions[0];
      if (field === 'notificationContent') {
        action.type = 'SendNotification';
        action.notificationContent = '&image=""';
        action.notificationChannel = 'windows';
      }
      const path = `actions[0].${field}`;
      const first = f.subject.saveTask();
      f.requests.at(-1).resolve(new Response(JSON.stringify({ errors: { [path]: ['首次错误'] } }), { status: 400 }));
      await first;
      const key = f.subject.actionKey(action);
      const messages = [];
      stop = vue.watchEffect(() => messages.push(f.subject.actionErrors.get(action)?.message));
      const retry = f.subject.saveTask();
      await vue.nextTick();
      assert.equal(f.subject.actionErrors.get(action)?.message, '首次错误', field);
      const body = Promise.withResolvers();
      f.requests.at(-1).resolve({ ok: false, status: 400, json: () => body.promise });
      await setImmediate();
      assert.equal(f.subject.actionErrors.get(action)?.message, '首次错误', field);
      body.resolve({ errors: { [path]: ['更新后的错误'] } });
      await retry;
      assert.deepEqual(messages, ['首次错误', '更新后的错误']);
      assert.equal(f.subject.editor.value, task);
      assert.equal(f.subject.actionKey(action), key);
      action[field] = 'changed';
      await vue.nextTick();
      assert.equal(f.subject.actionErrors.has(action), false);
    } finally { stop?.(); f.dispose(); }
  }
});

test('a new server validation result replaces previous field markers', async () => {
  const f = fixture();
  try {
    const task = openValidTaskEditor(f);
    let pending = f.subject.saveTask();
    f.requests.at(-1).resolve(new Response(JSON.stringify({ errors: { 'actions[0].programPath': ['程序路径无效'] } }), { status: 400 }));
    await pending;
    pending = f.subject.saveTask();
    f.requests.at(-1).resolve(new Response(JSON.stringify({ errors: { name: ['同名任务已存在'] } }), { status: 409 }));
    await pending;
    assert.equal(f.subject.actionErrors.size, 0);
    assert.equal(f.subject.fieldErrors.value.name, '同名任务已存在');
    assert.equal(f.subject.editor.value, task);
  } finally { f.dispose(); }
});

test('process event starts unselected, is required and is omitted from unrelated trigger payloads', async () => {
  const f = fixture();
  try {
    const task = openValidTaskEditor(f);
    assert.equal(task.trigger.event, '');
    task.trigger.type = 'Process';
    task.trigger.processPaths = ['C:\\app.exe'];
    await f.subject.saveTask();
    assert.equal(f.subject.fieldErrors.value.event, '请选择事件');
    assert.equal(f.requests.length, 0);
    for (const event of ['Started', 'Exited']) {
      task.trigger.event = event;
      assert.equal(f.subject.fieldErrors.value.event, undefined);
      const pending = f.subject.saveTask();
      const request = f.requests.at(-1);
      const body = JSON.parse(request.init.body);
      assert.equal(body.trigger.type, 'Process');
      assert.equal(body.trigger.event, event);
      assert.deepEqual(body.trigger.processPaths, ['C:\\app.exe']);
      request.resolve({ ok: false, status: 500, json: async () => ({ detail: '保存失败' }) });
      await pending;
    }
    task.trigger.type = 'Cron';
    const pending = f.subject.saveTask();
    const request = f.requests.at(-1);
    assert.equal('event' in JSON.parse(request.init.body).trigger, false);
    request.resolve({ ok: false, status: 500, json: async () => ({ detail: '保存失败' }) });
    await pending;
  } finally { f.dispose(); }
});

test('process event edits discard late validation feedback for the previous selection', async () => {
  const f = fixture();
  try {
    const task = openValidTaskEditor(f);
    task.trigger.type = 'Process';
    task.trigger.event = 'Started';
    task.trigger.processPaths = ['C:\\app.exe'];
    const pending = f.subject.saveTask();
    task.trigger.event = 'Exited';
    f.requests[0].resolve({ ok: false, status: 400, json: async () => ({ errors: { 'trigger.event': ['旧事件无效'] } }) });
    await pending;
    assert.equal(f.subject.fieldErrors.value.event, undefined);
    assert.equal(task.trigger.event, 'Exited');
  } finally { f.dispose(); }
});

test('late save errors only apply to unchanged fields in the same editor', async () => {
  const f = fixture();
  try {
    const task = openValidTaskEditor(f);
    const pending = f.subject.saveTask();
    task.name = 'New name';
    f.requests[0].resolve({ ok: false, status: 400, json: async () => ({ errors: {
      name: ['旧名称无效'], 'trigger.cron': ['Cron 表达式格式无效'],
    } }) });
    await pending;
    assert.equal(f.subject.fieldErrors.value.name, undefined);
    assert.equal(f.subject.fieldErrors.value.cron, 'Cron 表达式格式无效');
    const retry = f.subject.saveTask();
    f.subject.openTaskEditor();
    f.requests[1].resolve({ ok: false, status: 409, json: async () => ({ errors: { name: ['旧任务重名'] } }) });
    await retry;
    assert.equal(f.subject.fieldErrors.value.name, undefined);
  } finally { f.dispose(); }
});

test('save action errors follow submitted identities after reorder and ignore changed or deleted actions', async () => {
  for (const change of ['reorder', 'edit', 'delete']) {
    const f = fixture();
    try {
      const task = openValidTaskEditor(f);
      task.actions.push({ ...f.subject.createAction(), type: 'Delay', delaySeconds: 1 });
      const target = task.actions[0];
      const pending = f.subject.saveTask();
      if (change === 'reorder') f.subject.move(task.actions, 0, 1);
      if (change === 'edit') target.programPath = 'other.exe';
      if (change === 'delete') task.actions.splice(0, 1);
      f.requests[0].resolve({ ok: false, status: 400, json: async () => ({ errors: { 'actions[0].programPath': ['程序路径无效'] } }) });
      await pending;
      await vue.nextTick();
      assert.equal(f.subject.actionErrors.has(target), change === 'reorder');
      assert.equal(f.subject.actionErrors.has(task.actions[0]), false);
    } finally { f.dispose(); }
  }
});

test('server errors unstructured failures and malformed validation data remain toast-only', async () => {
  for (const [status, problem, message] of [
    [500, { detail: '保存失败', errors: { name: ['不应作为字段错误'] } }, '保存失败'],
    [403, { detail: '安全模式下不可执行此操作' }, '安全模式下不可执行此操作'],
    [400, { detail: '操作失败' }, '操作失败'],
    [400, { detail: '无效字段数据', errors: { name: '不是字符串数组' } }, '无效字段数据'],
  ]) {
    const f = fixture();
    try {
      openValidTaskEditor(f);
      const pending = f.subject.saveTask();
      f.requests[0].resolve({ ok: false, status, json: async () => problem });
      await pending;
      assert.equal(Object.keys(f.subject.fieldErrors.value).length, 0);
      assert.equal(f.subject.actionErrors.size, 0);
      assert.deepEqual(f.toasts, [message]);
    } finally { f.dispose(); }
  }
});

test('unmount during a save error body read cannot add fields or display a toast', async () => {
  const f = fixture();
  try {
    openValidTaskEditor(f);
    let finishBody;
    const pending = f.subject.saveTask();
    f.requests[0].resolve({ ok: false, status: 400, json: () => new Promise(resolve => { finishBody = resolve; }) });
    await setImmediate();
    assert.equal(typeof finishBody, 'function');
    f.dispose();
    finishBody({ errors: { name: ['名称无效'] } });
    await pending;
    assert.equal(Object.keys(f.subject.fieldErrors.value).length, 0);
    assert.deepEqual(f.toasts, []);
  } finally { f.dispose(); }
});

test('notification action defaults to enabled channels and preserves explicit channel selection', () => {
  const f = fixture();
  try {
    const action = f.subject.createAction();
    action.type = 'SendNotification';
    action.notificationContent = '测试通知';
    assert.equal(action.notificationChannel, '');
    const task = { name: 'Task', trigger: { type: 'WinPilotStartup' }, actions: [action] };
    for (const channel of ['', 'QQBot']) {
      action.notificationChannel = channel;
      assert.equal(Object.keys(f.subject.validate(task)).length, 0);
      assert.equal(f.subject.actionPayload(action).notificationChannel, channel);
    }
  } finally { f.dispose(); }
});

test('tag and close process actions report missing fields before saving', () => {
  const f = fixture();
  try {
    const action = f.subject.createAction();
    const task = { name: 'Task', trigger: { type: 'WinPilotStartup' }, actions: [action] };
    for (const [type, field, message] of [
      ['StartTag', 'tagName', '请选择快捷标签'],
      ['CloseProcess', 'processName', '请输入进程名'],
    ]) {
      action.type = type;
      for (const value of ['', '   ']) {
        action[field] = value;
        assert.equal(f.subject.validate(task)['action-0'], message);
      }
      action[field] = 'Example';
      assert.equal(Object.keys(f.subject.validate(task)).length, 0);
    }
  } finally { f.dispose(); }
});

test('extension trigger errors use the declared parameter label and input type', () => {
  const f = fixture();
  try {
    const parameter = { key: 'target', label: '目标配置', type: 'Select', required: true, options: [] };
    f.subject.registeredTriggers.value = [{ pluginId: 'example', triggerId: 'event', parameters: [parameter] }];
    const task = {
      name: 'Task',
      trigger: { type: 'Extension', pluginId: 'example', triggerId: 'event', parameters: {} },
      actions: [],
    };
    assert.equal(f.subject.validate(task).extension, '请选择目标配置');
    parameter.type = 'Text';
    parameter.label = '匹配内容';
    assert.equal(f.subject.validate(task).extension, '请输入匹配内容');
    task.trigger.parameters.target = 'Example';
    assert.equal(f.subject.validate(task).extension, undefined);
    parameter.type = 'Select';
    parameter.label = '目标配置';
    const action = { ...f.subject.createAction(), type: 'Extension', pluginId: 'example', actionId: 'run', parameters: { target: 'Example' } };
    task.actions = [action];
    f.subject.registeredActions.value = [{ pluginId: 'example', actionId: 'run', parameters: [parameter] }];
    assert.equal(f.subject.validate(task).extension, '目标配置包含不可用配置，请重新选择');
    assert.equal(f.subject.validate(task)['action-0'], '目标配置包含不可用配置，请重新选择');
    task.trigger.parameters.target = '';
    action.parameters.target = '';
    assert.equal(f.subject.validate(task).extension, '请选择目标配置');
    assert.equal(f.subject.validate(task)['action-0'], '请选择目标配置');
  } finally { f.dispose(); }
});

test('delay is prefilled with five seconds and requires integer seconds from 1 to 86400 before saving', () => {
  const f = fixture();
  try {
    const action = f.subject.createAction();
    assert.equal(action.delaySeconds, 5);
    action.type = 'Delay';
    const task = { name: 'Delay', trigger: { type: 'WinPilotStartup' }, actions: [action] };
    for (const value of ['', 0, -1, 1.5]) {
      action.delaySeconds = value;
      assert.equal(f.subject.validate(task)['action-0'], '请输入有效的延迟秒数');
    }
    action.delaySeconds = 1;
    assert.equal(Object.keys(f.subject.validate(task)).length, 0);
    action.delaySeconds = 86400;
    assert.equal(Object.keys(f.subject.validate(task)).length, 0);
    action.delaySeconds = 86401;
    assert.equal(f.subject.validate(task)['action-0'], '延迟时间不能超过 86400 秒（24 小时）');
  } finally { f.dispose(); }
});

test('delay prefill preserves entered seconds and explicit saved values including empty values', () => {
  const f = fixture();
  try {
    f.subject.openTaskEditor({ name: 'Saved', trigger: { type: 'WinPilotStartup' }, actions: [{ type: 'StartProgram', programPath: 'app.exe' }] });
    const action = f.subject.editor.value.actions[0];
    assert.equal(action.delaySeconds, 5);
    assert.equal(f.subject.editor.value.trigger.processPaths.length, 0);
    f.subject.selectActionType(0, 'Delay');
    assert.equal(action.delaySeconds, 5);
    action.delaySeconds = 17;
    f.subject.selectActionType(0, 'StartProgram');
    f.subject.selectActionType(0, 'Delay');
    assert.equal(action.delaySeconds, 17);
    for (const saved of [30, '']) {
      f.subject.openTaskEditor({ name: 'Saved', trigger: { type: 'WinPilotStartup' }, actions: [{ type: 'Delay', delaySeconds: saved }] });
      assert.equal(f.subject.editor.value.actions[0].delaySeconds, saved);
    }
  } finally { f.dispose(); }
});

test('action payloads exclude unrelated editor fields and keep current action settings', () => {
  const f = fixture();
  try {
    const action = { ...f.subject.createAction(), programPath: 'app.exe', delaySeconds: 5 };
    assert.equal('delaySeconds' in f.subject.actionPayload(action), false);
    action.type = 'Delay';
    assert.deepEqual(JSON.parse(JSON.stringify(f.subject.actionPayload(action))), { type: 'Delay', delaySeconds: 5 });
    action.type = 'CloseProcess';
    action.processName = 'app.exe';
    action.force = true;
    action.currentUserOnly = true;
    assert.deepEqual(JSON.parse(JSON.stringify(f.subject.actionPayload(action))),
      { type: 'CloseProcess', processName: 'app.exe', force: true, currentUserOnly: true });
  } finally { f.dispose(); }
});

test('deleted trigger configuration is unavailable in task warnings and overview', () => {
  const f = fixture();
  try {
    f.subject.registeredTriggers.value = [{ pluginId: 'plugin', triggerId: 'log', triggerName: '日志触发', available: true, parameters: [{ key: 'rule', type: 'Select', options: [{ value: 'existing', label: '现有配置' }] }] }];
    const task = { trigger: { type: 'Extension', pluginId: 'plugin', triggerId: 'log', parameters: { rule: 'deleted' } }, actions: [] };
    assert.equal(f.subject.taskWarningText(task), '插件触发条件不可用');
    assert.match(f.subject.extensionTriggerLabel(task.trigger), /不可用/);
    task.trigger.parameters.rule = 'existing';
    assert.equal(f.subject.taskWarningText(task), '');
    assert.match(f.subject.extensionTriggerLabel(task.trigger), /现有配置/);
  } finally { f.dispose(); }
});

test('overview displays saved values while querying and deduplicates repeated actions and editor requests', async () => {
  const f = fixture();
  try {
    f.open();
    assert.equal(f.subject.extensionActionLabel(f.action), '运行任务 - saved');
    f.subject.editor.value = { actions: [f.action] };
    const loading = f.subject.loadActionOptions(0, 'name');
    assert.equal(f.requests.length, 1);
    await f.reply([{ value: 'saved', label: '已保存的任务' }]);
    assert.equal(await loading, true);
    assert.equal(f.subject.extensionActionLabel(f.action), '运行任务 - 已保存的任务');
  } finally { f.dispose(); }
});

test('failed deletion retains its dialog and repeated clicks share one request', async () => {
  const f = fixture();
  try {
    f.subject.deleteTarget.value = { name: 'test' };
    const pending = f.subject.deleteTask();
    await f.subject.deleteTask();
    assert.equal(f.requests.length, 1);
    await f.reply({ detail: 'denied' }, false);
    await pending;
    assert.equal(f.subject.deleteTarget.value.name, 'test');
    assert.deepEqual(f.toasts, ['denied']);
    assert.equal(f.subject.pendingOperations.size, 0);
  } finally { f.dispose(); }
});

test('trigger selection updates the declared key rather than a plugin-specific key', () => {
  const f = fixture();
  try {
    f.subject.editor.value = { trigger: { parameters: {} }, actions: [] };
    f.subject.selectTriggerParameter('target', 'value');
    assert.equal(f.subject.editor.value.trigger.parameters.target, 'value');
    assert.equal(f.subject.editor.value.trigger.parameters.conditionId, undefined);
  } finally { f.dispose(); }
});

test('parameter defaults are independent from registered schemas and other actions', () => {
  const f = fixture();
  try {
    const schema = vue.reactive([{ key: 'values', type: 'MultiCheckbox', defaultValue: ['first'] }]);
    const first = f.subject.parameterDefaults(schema);
    const second = f.subject.parameterDefaults(schema);
    first.values.push('second');
    assert.equal(second.values.join(','), 'first');
    assert.equal(schema[0].defaultValue.join(','), 'first');
  } finally { f.dispose(); }
});

test('only a successful query confirming absence marks a dynamic selection unavailable', async () => {
  const f = fixture();
  try {
    f.open();
    await f.reply([]);
    assert.equal(f.subject.extensionActionLabel(f.action), '运行任务 - saved（不可用）');
  } finally { f.dispose(); }
});

test('failed queries keep saved values and report failure once', async () => {
  const f = fixture();
  try {
    f.open();
    await f.reply([], false);
    assert.equal(f.subject.extensionActionLabel(f.action), '运行任务 - saved');
    assert.equal(f.toasts.length, 1);
    f.subject.detailTarget.value = undefined;
    f.open();
    assert.equal(f.requests.length, 2);
    await f.reply([{ value: 'saved', label: '恢复正常' }]);
    assert.equal(f.subject.extensionActionLabel(f.action), '运行任务 - 恢复正常');
  } finally { f.dispose(); }
});

test('closing or unmounting an overview ignores late results and errors', async () => {
  for (const close of [f => { f.subject.detailTarget.value = undefined; }, f => f.dispose()]) {
    const f = fixture();
    try {
      f.open();
      close(f);
      await f.reply([{ value: 'saved', label: '旧结果' }]);
      assert.equal(f.subject.detailActionOptions.value.size, 0);
      assert.equal(f.toasts.length, 0);
    } finally { f.dispose(); }
  }
});

test('task warnings distinguish unavailable triggers and plugin actions without judging unknown dynamic choices', () => {
  const f = fixture();
  try {
    const task = { trigger: { type: 'Extension', pluginId: 'plugin', triggerId: 'trigger' }, actions: [f.action] };
    assert.equal(f.subject.taskWarningText(task), '插件触发条件不可用');
    f.subject.registeredActions.value[0].available = false;
    assert.equal(f.subject.taskWarningText(task), '插件触发条件不可用\n部分插件动作不可用');
    f.subject.registeredTriggers.value = [{ pluginId: 'plugin', triggerId: 'trigger', available: true }];
    assert.equal(f.subject.taskWarningText(task), '部分插件动作不可用');
    f.subject.registeredActions.value[0].available = true;
    assert.equal(f.subject.taskWarningText(task), '');
    f.subject.registeredActions.value[0].dynamicOptions = false;
    assert.equal(f.subject.taskWarningText(task), '部分插件动作不可用');
    f.subject.registeredActions.value[0].parameters[0].options = [{ value: 'saved', label: '正常' }];
    assert.equal(f.subject.taskWarningText(task), '');
    f.subject.registeredActions.value = [];
    assert.equal(f.subject.taskWarningText(task), '部分插件动作不可用');
  } finally { f.dispose(); }
});

test('disabled or missing plugin extensions remain unavailable in overview without querying options', async () => {
  const f = fixture();
  try {
    f.subject.registeredActions.value[0].available = false;
    f.open();
    assert.equal(f.requests.length, 0);
    assert.equal(f.subject.extensionActionLabel(f.action), '运行任务（不可用）');
    f.subject.editor.value = { actions: [f.action] };
    assert.equal(await f.subject.loadActionOptions(0, 'name'), false);
    assert.equal(f.requests.length, 0);
    const trigger = { pluginId: 'plugin', triggerId: 'trigger' };
    f.subject.registeredTriggers.value = [{ ...trigger, pluginDisplayName: '插件', triggerName: '触发条件', available: false, parameters: [] }];
    assert.equal(f.subject.extensionTriggerLabel(trigger), '扩展 · 插件 · 触发条件（不可用）');
    f.subject.registeredActions.value = [];
    assert.equal(f.subject.extensionActionLabel(f.action), '扩展 · plugin · action（不可用）');
    f.subject.registeredTriggers.value = [];
    assert.equal(f.subject.extensionTriggerLabel(trigger), '扩展 · plugin · trigger（不可用）');
  } finally { f.dispose(); }
});

test('task dynamic option requests stay tracked from headers through body completion', async () => {
  const f = fixture();
  const body = Promise.withResolvers();
  let loading, bodyStarted = false;
  try {
    f.subject.editor.value = { actions: [f.action] };
    loading = f.subject.loadActionOptions(0, 'name');
    assert.equal(f.subject.requestControllers.size, 1);
    f.requests[0].resolve({ ok: true, json: () => { bodyStarted = true; return body.promise; } });
    await setImmediate();
    assert.equal(bodyStarted, true);
    assert.equal(f.subject.requestControllers.size, 1);
    body.resolve([{ value: 'saved', label: '已保存的任务' }]);
    assert.equal(await loading, true);
    assert.equal(f.subject.requestControllers.size, 0);
  } finally {
    body.resolve([{ value: 'saved', label: '已保存的任务' }]);
    f.dispose();
    if (loading) await loading;
  }
});

test('task unmount cancels the underlying dynamic option body after headers arrive', async () => {
  const f = fixture();
  const body = Promise.withResolvers();
  let loading, call, bodyStarted = false, bodyCancelled = false;
  const cancelBody = () => {
    bodyCancelled = true;
    body.reject(new DOMException('正文读取已取消', 'AbortError'));
  };
  try {
    f.subject.editor.value = { actions: [f.action] };
    loading = f.subject.loadActionOptions(0, 'name');
    call = f.requests[0];
    call.resolve({ ok: true, json: () => {
      bodyStarted = true;
      call.init.signal.addEventListener('abort', cancelBody, { once: true });
      if (call.init.signal.aborted) cancelBody();
      return body.promise;
    } });
    await setImmediate();
    assert.equal(bodyStarted, true);
    assert.equal(call.init.signal.aborted, false);
    f.dispose();
    assert.deepEqual({ aborted: call.init.signal.aborted, bodyCancelled }, { aborted: true, bodyCancelled: true });
    assert.equal(await loading, false);
    assert.equal(f.subject.requestControllers.size, 0);
    assert.equal(f.toasts.length, 0);
  } finally {
    body.resolve([{ value: 'saved', label: '迟到的任务' }]);
    call?.init.signal.removeEventListener('abort', cancelBody);
    f.dispose();
    if (loading) await loading;
  }
});

test('completed task dynamic option bodies release tracking and preserve their result', async () => {
  const f = fixture();
  const body = Promise.withResolvers();
  let loading;
  try {
    f.subject.editor.value = { actions: [f.action] };
    loading = f.subject.loadActionOptions(0, 'name');
    assert.equal(f.subject.requestControllers.size, 1);
    f.requests[0].resolve({ ok: true, json: () => body.promise });
    await setImmediate();
    body.resolve([{ value: 'saved', label: '已保存的任务' }]);
    assert.equal(await loading, true);
    assert.equal(f.subject.requestControllers.size, 0);
    assert.equal(f.subject.registeredActions.value[0].parameters[0].options[0].label, '已保存的任务');
    assert.equal(f.toasts.length, 0);
  } finally {
    body.resolve([{ value: 'saved', label: '已保存的任务' }]);
    f.dispose();
    if (loading) await loading;
  }
});
