import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { readFileSync } from 'node:fs';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as models from '../../src/features/tasks/taskModels.ts';

const validation = { exports: {}, require: () => models };
runInNewContext(loadScriptModule(new URL('../../src/features/tasks/taskValidation.ts', import.meta.url)), validation);
const { validateTask, unavailableSelectValue } = validation.exports;

function taskWith(action) {
  return { name: 'Example', enabled: true, trigger: { ...models.createTrigger(), cron: '0 30 8 * * *' }, actions: [action] };
}

test('save validation rejects invalid task names but preserves trimming and valid names', () => {
  const task = taskWith(models.createAction());
  for (const name of ['bad?name', 'bad/name', 'bad\\name', 'bad:name', 'bad\u0001name', 'name.']) {
    task.name = name;
    assert.equal(validateTask(task, [], []).name, '任务名称无效', name);
  }
  for (const name of ['任务一', ' Example ', 'Example.txt', '任务（测试）']) {
    task.name = name;
    assert.equal(validateTask(task, [], []).name, undefined, name);
  }
  task.name = ' ';
  assert.equal(validateTask(task, [], []).name, '请输入任务名称');
});

test('save validation rejects incomplete extension IDs', () => {
  const task = taskWith({ ...models.createAction(), type: 'Extension', pluginId: ' ', actionId: 'run' });
  assert.equal(validateTask(task, [], [])['action-0'], '请选择插件和动作');
  task.trigger = { ...models.createTrigger(), type: 'Extension', pluginId: 'plugin', triggerId: ' ' };
  assert.equal(validateTask(task, [], []).extension, '请选择插件和触发条件');
});

test('task factories allocate independent editable collections and only prefill new delays', () => {
  const first = models.createAction();
  const second = models.createAction();
  first.keys.push('A');
  first.parameters.target = 'changed';
  assert.deepEqual(second.keys, []);
  assert.deepEqual(second.parameters, {});
  assert.equal(second.delaySeconds, 5);
  first.delaySeconds = '';
  assert.equal(first.delaySeconds, '');
  const trigger = models.createTrigger();
  trigger.processPaths.push('C:\\app.exe');
  assert.deepEqual(models.createTrigger().processPaths, []);
});

test('parameter defaults clone nested values independently for every use', () => {
  const definitions = [
    { key: 'selected', type: 'MultiCheckbox', defaultValue: ['first'] },
    { key: 'nested', type: 'Text', defaultValue: { values: ['original'] } },
    { key: 'enabled', type: 'Checkbox' },
  ];
  const first = models.parameterDefaults(definitions);
  const second = models.parameterDefaults(definitions);
  first.selected.push('second');
  first.nested.values.push('changed');
  assert.deepEqual(second, { selected: ['first'], nested: { values: ['original'] }, enabled: false });
  assert.deepEqual(definitions[0].defaultValue, ['first']);
});

test('program action exposes launch checkboxes with the existing process options layout', () => {
  const source = readFileSync(new URL('../../src/features/tasks/TaskActionEditor.vue', import.meta.url), 'utf8');
  const section = source.match(/<template v-else-if="action.type === 'StartProgram'">([\s\S]*?)<\/template>/)?.[1];
  assert.match(section, /<div class="task-process-options">/);
  assert.match(section, /<input v-model="action.runElevated" type="checkbox" \/>\s*<span>使用最高权限运行<\/span>/);
  assert.match(section, /<input v-model="action.silent" type="checkbox" \/>\s*<span>静默执行<\/span>/);
  const action = { ...models.createAction(), type: 'StartProgram' };
  assert.equal(action.runElevated, false);
  assert.equal(action.silent, false);
  action.runElevated = true;
  action.silent = true;
  const payload = models.actionPayload(action);
  assert.equal(payload.runElevated, true);
  assert.equal(payload.silent, true);
});

test('each action payload excludes fields belonging to other action types', () => {
  const fields = {
    StartProgram: ['programPath', 'arguments', 'workingDirectory', 'runElevated', 'silent'],
    StartTag: ['tagName'], EndProcess: ['processName', 'currentUserOnly'],
    Delay: ['delaySeconds'], Keyboard: ['keys'], Shell: ['shell', 'command'],
    SendNotification: ['notificationContent', 'notificationChannel'],
    TaskState: ['taskName', 'state'], Extension: ['pluginId', 'actionId', 'parameters'],
  };
  for (const [type, keys] of Object.entries(fields)) {
    const action = { ...models.createAction(), type };
    assert.deepEqual(Object.keys(models.actionPayload(action)).sort(), ['type', ...keys].sort(), type);
    action.continueOnFailure = true;
    assert.equal(models.actionPayload(action).continueOnFailure, true);
  }
});

test('validation uses explicit plugin definitions without mutating tasks or schemas', () => {
  const task = taskWith({ ...models.createAction(), type: 'Extension', pluginId: 'chosen', actionId: 'run', parameters: { target: 'valid', enabled: false, count: 0, optional: [] } });
  const definitions = [
    { pluginId: 'other', actionId: 'run', parameters: [{ key: 'missing', label: '其他插件参数', type: 'Text', required: true }] },
    { pluginId: 'chosen', actionId: 'run', parameters: [
      { key: 'target', label: '目标', type: 'Select', required: true, options: [{ value: 'valid', label: '有效' }] },
      { key: 'enabled', label: '启用', type: 'Checkbox', required: true },
      { key: 'count', label: '数量', type: 'Number', required: true },
      { key: 'optional', label: '可选项', type: 'MultiCheckbox', required: false, options: [] },
    ] },
  ];
  const before = JSON.stringify({ task, definitions });
  assert.equal(Object.keys(validateTask(task, [], definitions)).length, 0);
  assert.equal(JSON.stringify({ task, definitions }), before);
  task.actions[0].parameters.target = 'removed';
  assert.equal(validateTask(task, [], definitions)['action-0'], '目标包含不可用配置，请重新选择');
  assert.equal(unavailableSelectValue(definitions[1].parameters[0], 'removed'), 'removed');
});

test('pure validation preserves trigger and built-in action field errors', () => {
  const task = taskWith({ ...models.createAction(), type: 'TaskState' });
  task.trigger = { ...models.createTrigger(), type: 'Process', processPaths: ['relative.exe'] };
  const errors = validateTask(task, [], []);
  assert.equal(errors.event, '请选择事件');
  assert.equal(errors.processPaths, '程序路径必须是绝对路径');
  assert.equal(errors['action-0'], '请选择任务');
  task.actions[0].taskName = 'target';
  assert.equal(validateTask(task, [], [])['action-0'], '请选择状态');
});

test('process trigger paths require Windows absolute forms while action filesystem checks remain server-side', () => {
  const task = taskWith({ ...models.createAction(), type: 'StartProgram', programPath: 'app.exe' });
  task.trigger = { ...models.createTrigger(), type: 'Process', event: 'Started' };
  for (const path of ['C:\\app.exe', 'C:/app.exe', '\\\\server\\share\\app.exe']) {
    task.trigger.processPaths = [path];
    assert.equal(Object.keys(validateTask(task, [], [])).length, 0, path);
  }
  for (const path of ['app.exe', 'C:app.exe', '\\app.exe']) {
    task.trigger.processPaths = [path];
    assert.equal(validateTask(task, [], []).processPaths, '程序路径必须是绝对路径', path);
  }
});
