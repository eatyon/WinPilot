import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';

function fixture() {
  const code = loadScriptModule(new URL('../../src/features/tasks/TasksView.vue', import.meta.url),
    'tasks, registeredActions, registeredTriggers, detailTarget, editor, deleteTarget, runTarget, extensionActionLabel, extensionTriggerLabel, loadActionOptions, detailActionOptions, taskWarningText, loadTasks, saveTask, deleteTask, toggleTaskEnabled, runTask, selectTriggerParameter, parameterDefaults, pendingOperations, createAction, validate, selectActionType, openTaskEditor, actionPayload');
  const requests = [];
  const toasts = [];
  let unmount;
  const context = {
    exports: {}, AbortController, HTMLInputElement: class {},
    require: name => {
      if (name === 'vue') return { ...vue, onMounted: () => {}, onBeforeUnmount: callback => { unmount = callback; } };
      if (name.endsWith('/client')) return { apiFetch: (url, init) => new Promise(resolve => requests.push({ url, init, resolve })), isAbortError: error => error?.name === 'AbortError', userErrorMessage: error => error.message, WinPilotConnectionError: class extends Error {} };
      if (name.endsWith('/toast')) return { showToast: text => toasts.push(text) };
      if (name.endsWith('/safeMode')) return { safeModeEnabled: vue.ref(false) };
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

test('delay starts empty and requires a positive integer before saving', () => {
  const f = fixture();
  try {
    const action = f.subject.createAction();
    assert.equal(action.delaySeconds, '');
    action.type = 'Delay';
    const task = { name: 'Delay', trigger: { type: 'WinPilotStartup' }, actions: [action] };
    for (const value of ['', 0, -1, 1.5]) {
      action.delaySeconds = value;
      assert.equal(f.subject.validate(task)['action-0'], '请输入有效的延迟秒数');
    }
    action.delaySeconds = 1;
    assert.equal(Object.keys(f.subject.validate(task)).length, 0);
  } finally { f.dispose(); }
});

test('sparse saved actions initialize empty delay while retaining entered seconds', () => {
  const f = fixture();
  try {
    f.subject.openTaskEditor({ name: 'Saved', trigger: { type: 'WinPilotStartup' }, actions: [{ type: 'StartProgram', programPath: 'app.exe' }] });
    const action = f.subject.editor.value.actions[0];
    assert.equal(action.delaySeconds, '');
    assert.equal(f.subject.editor.value.trigger.processPaths.length, 0);
    f.subject.selectActionType(0, 'Delay');
    assert.equal(action.delaySeconds, '');
    action.delaySeconds = 5;
    f.subject.selectActionType(0, 'StartProgram');
    f.subject.selectActionType(0, 'Delay');
    assert.equal(action.delaySeconds, 5);
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

test('page disposal cancels writes and reads without late updates or notifications', async () => {
  const f = fixture();
  f.subject.runTarget.value = { name: 'test' };
  const running = f.subject.runTask();
  const loading = f.subject.loadTasks();
  const reading = f.subject.loadActionOptions(0, 'name');
  f.dispose();
  assert.ok(f.requests.every(request => request.init.signal.aborted));
  for (const request of f.requests) request.resolve({ ok: true, json: async () => [] });
  await Promise.all([running, loading, reading]);
  assert.equal(f.subject.tasks.value.length, 0);
  assert.equal(f.subject.runTarget.value.name, 'test');
  assert.deepEqual(f.toasts, []);
});

test('an older page loadTasks cannot overwrite a newer result', async () => {
  const f = fixture();
  try {
    const old = f.subject.loadTasks();
    const latest = f.subject.loadTasks();
    assert.ok(f.requests.slice(0, 4).every(request => request.init.signal.aborted));
    for (const request of f.requests.slice(4)) request.resolve({ ok: true, json: async () => [{ name: 'new' }] });
    await latest;
    for (const request of f.requests.slice(0, 4)) request.resolve({ ok: true, json: async () => [{ name: 'old' }] });
    await old;
    assert.equal(f.subject.tasks.value[0].name, 'new');
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

test('separate task operations do not cancel each other and toggleTaskEnabled failures are visible', async () => {
  const f = fixture();
  try {
    f.subject.runTarget.value = { name: 'run' };
    const running = f.subject.runTask();
    const toggling = f.subject.toggleTaskEnabled({ name: 'toggleTaskEnabled', enabled: true });
    await f.subject.toggleTaskEnabled({ name: 'toggleTaskEnabled', enabled: true });
    assert.equal(f.requests.length, 2);
    assert.ok(f.requests.every(request => !request.init.signal.aborted));
    f.requests[0].resolve({ ok: true });
    f.requests[1].resolve({ ok: false, json: async () => ({ detail: 'cannot saveTask' }) });
    await Promise.all([running, toggling]);
    assert.equal(f.subject.runTarget.value, undefined);
    assert.deepEqual(f.toasts, ['cannot saveTask']);
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
