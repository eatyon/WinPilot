import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { reactive, computed } from 'vue';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as taskModels from '../../src/features/tasks/taskModels.ts';

const component = new URL('../../src/features/tasks/TaskActionEditor.vue', import.meta.url);
const source = readFileSync(component, 'utf8');
const script = loadScriptModule(component, 'actionPlugins,actionDefinitions,actionDefinition');
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
    require: name => name === 'vue' ? { computed }
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

test('action editor delegates selection and network work while the page owns ordering and errors', () => {
  const page = readFileSync(new URL('../../src/features/tasks/TasksView.vue', import.meta.url), 'utf8');
  assert.ok(source.includes("emit('select-type', $event)"));
  assert.ok(source.includes("emit('select-extension-action', $event)"));
  assert.ok(source.includes("emit('show-variables')"));
  assert.ok(source.includes(':before-open="() => loadOptions(parameter.key)"'));
  assert.ok(source.includes('<small v-if="error" class="field-error">'));
  assert.doesNotMatch(source, /AbortController|apiFetch|onBeforeUnmount|fetch\(/);
  assert.ok(page.includes(':error="actionErrors.get(action)?.message"'));
  assert.ok(page.includes(':load-options="parameterKey => loadActionOptions(index, parameterKey)"'));
  assert.ok(page.includes('@select-type="selectActionType(index, $event)"'));
  assert.ok(page.includes('@select-extension-action="selectExtensionAction(index, $event)"'));
  assert.ok(page.includes(':key="actionKey(action)"'));
});

test('built-in action inputs keep visible labels after values are entered', () => {
  const labels = [...source.matchAll(/<label\b[^>]*class="field-control"[^>]*>([\s\S]*?)<\/label>/g)].map(match => match[1]);
  for (const [field, title] of [
    ['programPath', '程序 / 脚本路径 *'], ['arguments', '参数'], ['workingDirectory', '起始目录'],
    ['tagName', '标签 *'], ['processName', '进程名 *'], ['delaySeconds', '秒数 *'],
    ['shell', '终端类型 *'], ['command', '命令 *'],
  ]) {
    assert.ok(labels.some(label => label.includes(`<span>${title}</span>`) && label.includes(`="action.${field}"`)), title);
  }
  assert.ok(source.includes('v-model="action.workingDirectory" placeholder="可留空"'));
});

test('delay keeps its action name and uses the seconds field title', () => {
  const action = { ...taskModels.createAction(), type: 'Delay' };
  assert.equal(taskModels.actionOptions.find(([type]) => type === 'Delay')[1], '延迟');
  assert.deepEqual(taskModels.actionPayload(action), { type: 'Delay', delaySeconds: 5 });
  assert.ok(source.includes('aria-label="秒数"'));
  const page = readFileSync(new URL('../../src/features/tasks/TasksView.vue', import.meta.url), 'utf8');
  assert.ok(page.includes('`延迟 ${action.delaySeconds || 0} 秒`'));
});

test('tag and channel pickers preserve saved choices and retry through the page-owned resource loader', () => {
  assert.equal([...source.matchAll(/:before-open="loadResource"/g)].length, 2);
  assert.equal([...source.matchAll(/:disabled="resourceState\?\.loading"/g)].length, 2);
  assert.ok(source.includes('action.tagName && !tags.some'));
  assert.ok(source.includes('{ value: action.tagName, label: action.tagName }'));
  assert.ok(source.includes('action.notificationChannel && !channelOptions.some'));
  assert.ok(source.includes('{ value: action.notificationChannel, label: action.notificationChannel }'));
  assert.equal([...source.matchAll(/v-else-if="resourceState\?\.error"/g)].length, 2);
  const page = readFileSync(new URL('../../src/features/tasks/TasksView.vue', import.meta.url), 'utf8');
  assert.ok(page.includes(':resource-state="actionResource(action.type)"'));
  assert.ok(page.includes(':load-resource="() => loadActionResource(action.type)"'));
  assert.ok(page.includes('void loadTasks();\n  void loadExtensions();'));
});
