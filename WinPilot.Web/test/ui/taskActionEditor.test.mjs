import { readFeatureStyles } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { reactive, computed } from 'vue';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as taskModels from '../../src/features/tasks/taskModels.ts';
import { elements, attribute, binding } from '../support/template.mjs';

const component = new URL('../../src/features/tasks/TaskActionEditor.vue', import.meta.url);
const source = readFileSync(component, 'utf8');
const script = loadScriptModule(component, 'actionPlugins,actionDefinitions,actionDefinition,fieldError,unassignedError');
const validation = { exports: {}, require: () => taskModels };
runInNewContext(loadScriptModule(new URL('../../src/features/tasks/taskValidation.ts', import.meta.url)), validation);

function fixture() {
  const props = reactive({
    action: { ...taskModels.createAction(), type: 'Extension', pluginId: 'one', actionId: 'run' },
    tasks: [], tags: [], channelOptions: [], registeredActions: [], extensionsLoaded: true, error: '',
    resourceState: undefined, loadResource: async () => true,
    loadOptions: async () => true,
  });
  const context = {
    exports: {},
    require: name => name === 'vue' ? { computed, useId: () => 'action-test' }
      : name === './taskModels' ? taskModels
      : name === './taskValidation' ? validation.exports : {},
    defineProps: () => props,
    defineEmits: () => () => {},
  };
  runInNewContext(script, context);
  return { props, ...context.exports };
}

test('action plugin choices follow the current action and updated declarations', () => {
  const f = fixture();
  f.props.registeredActions = [
    { pluginId: 'one', actionId: 'run', actionName: 'First' },
    { pluginId: 'two', actionId: 'run', actionName: 'Second' },
    { pluginId: 'one', actionId: 'other', actionName: 'Other' },
  ];
  assert.deepEqual([...f.actionPlugins.value].map(item => item.pluginId), ['one', 'two']);
  assert.deepEqual([...f.actionDefinitions.value].map(item => item.actionName), ['First', 'Other']);
  assert.equal(f.actionDefinition.value.actionName, 'First');
  f.props.action.actionId = 'other';
  assert.equal(f.actionDefinition.value.actionName, 'Other');
  f.props.action.actionId = 'run';
  f.props.action.pluginId = 'two';
  assert.deepEqual([...f.actionDefinitions.value].map(item => item.actionName), ['Second']);
  assert.equal(f.actionDefinition.value.actionName, 'Second');
  f.props.registeredActions = [{ pluginId: 'two', actionId: 'run', actionName: 'Updated', dynamicOptions: true }];
  assert.equal(f.actionDefinition.value.actionName, 'Updated');
  assert.equal(f.actionDefinition.value.dynamicOptions, true);
  f.props.registeredActions = [];
  assert.equal(f.actionDefinitions.value.length, 0);
  assert.equal(f.actionDefinition.value, undefined);
});

test('notification variables remain at the right edge of the field heading', () => {
  const css = readFeatureStyles();
  assert.match(source, /class="notification-content-field"/);
  assert.match(source, /class="app-icon-button notification-variable-entry"/);
  assert.match(css, /\.notification-variable-entry\s*\{[^}]*position: absolute;[^}]*top: 0;[^}]*right: 0/);
});

test('action editor delegates selection and network work while the page owns ordering and errors', () => {
  const page = readFileSync(new URL('../../src/features/tasks/TasksView.vue', import.meta.url), 'utf8');
  assert.ok(source.includes("emit('select-type', $event)"));
  assert.ok(source.includes("emit('select-extension-action', $event)"));
  assert.ok(source.includes("emit('show-variables')"));
  assert.ok(source.includes(':before-open="() => loadOptions(parameter.key)"'));
  assert.ok(source.includes('<NCollapseTransition :show="Boolean(unassignedError)">'));
  assert.doesNotMatch(source, /AbortController|apiFetch|onBeforeUnmount|fetch\(/);
  assert.ok(page.includes(':error="actionErrors.get(action)?.message"'));
  assert.ok(page.includes(':load-options="(parameterKey) => loadActionOptions(index, parameterKey)"'));
  assert.ok(page.includes('@select-type="selectActionType(index, $event)"'));
  assert.ok(page.includes('@select-extension-action="selectExtensionAction(index, $event)"'));
  assert.ok(page.includes(':key="actionKey(action)"'));
});

test('built-in action inputs keep visible labels after values are entered', () => {
  const fields = elements(source, node => node.tag === 'NFormItem');
  for (const [field, title] of [
    ['programPath', '程序 / 脚本路径 *'], ['arguments', '参数'], ['workingDirectory', '起始目录'],
    ['tagName', '标签 *'], ['processName', '进程名 *'], ['delaySeconds', '秒数 *'],
    ['shell', '终端类型 *'], ['command', '命令 *'],
  ]) {
    const item = fields.find(item => attribute(item, 'data-task-field') === field);
    assert.ok(item, title);
    assert.equal(attribute(item, 'label'), title.replace(/ \*$/, ''));
    assert.equal(item.props.some(prop => prop.type === 6 && prop.name === 'required'), title.endsWith(' *'));
    assert.ok(item.loc.source.includes(`="action.${field}"`), title);
  }
  assert.match(source, /v-model:value="action.workingDirectory"\s+placeholder="可留空"/);
});

test('delay keeps its action name and uses the seconds field title', () => {
  const action = { ...taskModels.createAction(), type: 'Delay' };
  assert.equal(taskModels.actionOptions.find(([type]) => type === 'Delay')[1], '延迟');
  assert.deepEqual(taskModels.actionPayload(action), { type: 'Delay', delaySeconds: 5 });
  assert.ok(source.includes('aria-label="秒数"'));
  const presentation = readFileSync(new URL('../../src/features/tasks/taskPresentation.ts', import.meta.url), 'utf8');
  assert.ok(presentation.includes('`延迟 ${action.delaySeconds || 0} 秒`'));
});

test('tag and channel pickers preserve saved choices and retry through the page-owned resource loader', () => {
  assert.equal([...source.matchAll(/:before-open="loadResource"/g)].length, 2);
  assert.equal([...source.matchAll(/:disabled="resourceState\?\.loading"/g)].length, 2);
  assert.match(source, /action.tagName &&\s*!tags.some/);
  assert.ok(source.includes('{ value: action.tagName, label: action.tagName }'));
  assert.match(source, /action.notificationChannel &&\s*!channelOptions.some/);
  assert.ok(source.includes('{ value: action.notificationChannel, label: action.notificationChannel }'));
  const feedbackFields = fieldsWithResourceFeedback();
  assert.equal(feedbackFields.length, 2);
  for (const field of feedbackFields) {
    assert.match(field.loc.source, /resourceState\?\.loading \? 'status' : 'alert'/);
    assert.match(field.loc.source, /可展开选择器重试/);
  }
  const page = readFileSync(new URL('../../src/features/tasks/TasksView.vue', import.meta.url), 'utf8');
  assert.ok(page.includes(':resource-state="actionResource(action.type)"'));
  assert.ok(page.includes(':load-resource="() => loadActionResource(action.type)"'));
  assert.ok(page.includes('void loadTasks();\n  void loadExtensions();'));
});

function fieldsWithResourceFeedback() {
  return elements(source, node => node.tag === 'NFormItem' && (binding(node, 'show-feedback') ?? '').includes('resourceState?.loading'));
}

test('action field validation uses inline library feedback and reserves alerts for unassigned errors', () => {
  const fields = elements(source, node => node.tag === 'NFormItem');
  for (const item of fields) {
    assert.match(binding(item, 'show-feedback'), /fieldError\(/);
    assert.match(binding(item, 'validation-status'), /fieldError\(/);
    assert.ok(binding(item, 'feedback') || item.loc.source.includes('<template #feedback>'));
  }
  const f = fixture();
  f.props.error = '字段错误';
  for (const [type, field] of [
    ['StartProgram', 'programPath'], ['StartProgram', 'arguments'], ['StartProgram', 'workingDirectory'],
    ['StartTag', 'tagName'], ['EndProcess', 'processName'], ['Delay', 'delaySeconds'],
    ['Keyboard', 'keys'], ['Shell', 'command'], ['Shell', 'shell'],
    ['SendNotification', 'notificationContent'], ['SendNotification', 'notificationChannel'],
    ['TaskState', 'taskName'], ['TaskState', 'state'], ['Extension', 'pluginId'], ['Extension', 'actionId'],
  ]) {
    f.props.action.type = type;
    f.props.invalidField = field;
    assert.equal(f.fieldError(field), '字段错误');
    assert.equal(f.fieldError('other'), undefined);
    assert.equal(f.unassignedError.value, undefined);
  }
  f.props.action.pluginId = 'one';
  f.props.action.actionId = 'run';
  f.props.registeredActions = [{ pluginId: 'one', actionId: 'run', parameters: [{ key: 'target', type: 'Text' }] }];
  f.props.invalidField = 'parameters.target';
  assert.equal(f.unassignedError.value, undefined);
  assert.equal(f.fieldError('parameters.target'), '字段错误');
  f.props.invalidField = 'unknown';
  assert.equal(f.unassignedError.value, '字段错误');
  f.props.invalidField = undefined;
  assert.equal(f.unassignedError.value, '字段错误');
  f.props.error = '';
  assert.equal(f.unassignedError.value, undefined);
  const page = readFileSync(new URL('../../src/features/tasks/TasksView.vue', import.meta.url), 'utf8');
  assert.doesNotMatch(page, /<NAlert v-if="fieldErrors.extension"/);
  for (const field of ['pluginId', 'triggerId']) {
    assert.ok(page.includes(`:feedback="invalidTriggerField === '${field}' ? fieldErrors.extension : undefined"`));
  }
});
