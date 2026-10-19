<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, shallowReactive, toRaw, watch } from 'vue';
import AppIcon from '../../components/AppIcon.vue';
import AppModal from '../../components/AppModal.vue';
import AppSelect from '../../components/AppSelect.vue';
import AppMultiSelect from '../../components/AppMultiSelect.vue';
import TaskActionEditor from './TaskActionEditor.vue';
import TaskVariableHelp from './TaskVariableHelp.vue';
import CronEditor from './CronEditor.vue';
import { orderKeys } from '../desktop/keyboardLayout';
import { apiErrorMessage, readApiError, isAbortError, userErrorMessage, WinPilotConnectionError } from '../../api/client';
import { executeRequest } from '../../api/requestExecutor';
import { showToast } from '../../ui/toast';
import { focusInvalidInput } from '../../ui/focusInvalidInput';
import { safeModeEnabled, safeModeMessage } from '../security/safeMode';
import { taskStateOptions, createTrigger, createAction, parameterDefaults, actionPayload,
  multiSelectionLabel, multiParameterOptions, toggleMultiOption,
  type Trigger, type Action, type Task, type ListedTask, type RegisteredTrigger, type RegisteredAction, type RegisteredVariable,
  type ActionOption } from './taskModels';
import { validateTask, unavailableSelectValue, hasUnavailableParameter, invalidParameter } from './taskValidation';
const tasks = ref<ListedTask[]>([]),
  tasksLoaded = ref(false),
  tags = ref<{ name: string }[]>([]),
  registeredTriggers = ref<RegisteredTrigger[]>([]),
  registeredActions = ref<RegisteredAction[]>([]),
  registeredVariables = ref<RegisteredVariable[]>([]),
  editor = ref<Task>(),
  originalName = ref(''),
  detailTarget = ref<ListedTask>(),
  deleteTarget = ref<Task>(),
  runTarget = ref<Task>(),
  pageError = ref(''),
  fieldErrors = ref<Record<string, string>>({}),
  taskSearch = ref('');
const variableHelpOpen = ref(false),
  variableHelpLoading = ref(false),
  variableHelpError = ref('');
let isPageActive = true;
let loadController: AbortController | undefined;
let extensionsController: AbortController | undefined;
const extensionsLoaded = ref(false), extensionsLoading = ref(false), extensionsError = ref('');
let variableHelpController: AbortController | undefined;
const requestControllers = new Set<AbortController>();
const pendingOperations = shallowReactive(new Map<string, AbortController>());

async function runPageRequest<T>(url: string, init: RequestInit, handleResponse: (response: Response) => T | Promise<T>): Promise<T> {
  const controller = new AbortController();
  if (!isPageActive) controller.abort();
  requestControllers.add(controller);
  try {
    return await executeRequest(url, init, controller, handleResponse);
  } finally {
    requestControllers.delete(controller);
  }
}

async function performOperation(key: string, url: string, init: RequestInit, success: () => Promise<void> | void,
  readFailure?: (response: Response, signal: AbortSignal) => Promise<string>) {
  if (!isPageActive || pendingOperations.has(key)) return;
  const controller = new AbortController();
  pendingOperations.set(key, controller);
  try {
    await runPageRequest(url, { ...init, signal: controller.signal }, async response => {
      if (!response.ok) throw new Error(readFailure
        ? await readFailure(response, controller.signal) : await readApiError(response, '操作失败'));
    });
    if (isPageActive && !controller.signal.aborted) await success();
  } catch (cause) {
    if (isPageActive && !controller.signal.aborted && !isAbortError(cause)) {
      const message = userErrorMessage(cause, '操作失败');
      if (message) showToast(message);
    }
  } finally {
    if (pendingOperations.get(key) === controller) pendingOperations.delete(key);
  }
}

const actionKeys = new WeakMap<Action, symbol>();
function actionKey(action: Action) {
  const identity = toRaw(action);
  let key = actionKeys.get(identity);
  if (!key) {
    key = Symbol();
    actionKeys.set(identity, key);
  }
  return key;
}
const channelOptions = ref<{ value: string; label: string }[]>([{ value: '', label: '已启用渠道' }]);
const actionResources = reactive({
  StartTag: { loading: false, loaded: false, error: '' },
  SendNotification: { loading: false, loaded: false, error: '' },
});
const pendingActionResources = new Map<Action['type'], Promise<boolean>>();
function actionResource(type: Action['type']) {
  return type === 'StartTag' || type === 'SendNotification' ? actionResources[type] : undefined;
}
function loadActionResource(type: Action['type']): Promise<boolean> {
  const state = actionResource(type);
  if (!state || !isPageActive) return Promise.resolve(false);
  const pending = pendingActionResources.get(type);
  if (pending) return pending;
  if (state.loaded) return Promise.resolve(true);
  const message = type === 'StartTag' ? '无法读取快捷标签，请重试' : '无法读取通知渠道，请重试';
  state.loading = true;
  state.error = '';
  const request = (async () => {
    try {
      const result = await runPageRequest(type === 'StartTag' ? '/api/programs/tags' : '/api/notifications/channels', {}, async response => {
        if (!response.ok) throw new Error(await readApiError(response, message));
        return response.json();
      });
      if (!isPageActive) return false;
      if (type === 'StartTag') tags.value = result;
      else channelOptions.value = result;
      state.loaded = true;
      return true;
    } catch (cause) {
      if (isPageActive && !isAbortError(cause)) state.error = userErrorMessage(cause, message);
      return false;
    }
  })().finally(() => {
    if (isPageActive) state.loading = false;
    if (pendingActionResources.get(type) === request) pendingActionResources.delete(type);
  });
  pendingActionResources.set(type, request);
  return request;
}
watch(() => editor.value?.actions.map(action => action.type), types => {
  for (const type of new Set(types))
    if (type === 'StartTag' || type === 'SendNotification') void loadActionResource(type);
}, { flush: 'sync' });
const actionErrors = shallowReactive(new Map<Action, { message: string; snapshot: string; field?: string }>());
const actionValidationFields: Partial<Record<Action['type'], string>> = {
  StartProgram: 'programPath', StartTag: 'tagName', CloseProcess: 'processName', Delay: 'delaySeconds',
  Keyboard: 'keys', Shell: 'command', SendNotification: 'notificationContent',
};
const editorForm = ref<HTMLElement>();
const triggerErrorField = ref('');
const invalidProcessPath = computed(() => Math.max(0, editor.value?.trigger.processPaths
  .findIndex(path => path.trim() && !/^(?:[a-zA-Z]:[\\/]|\\\\)/.test(path.trim())) ?? -1));
const invalidTriggerField = computed(() => {
  if (!fieldErrors.value.extension || !editor.value) return undefined;
  const trigger = editor.value.trigger;
  if (triggerErrorField.value) return triggerErrorField.value;
  if (!trigger.pluginId) return 'pluginId';
  if (!trigger.triggerId) return 'triggerId';
  const parameter = invalidParameter(triggerDefinitionFor(trigger.triggerId)?.parameters, trigger.parameters);
  return parameter ? `parameters.${parameter.key}` : 'triggerId';
});
const inputSelector = 'input:not(:disabled), textarea:not(:disabled), .app-select-trigger:not(:disabled), .keyboard-expand-toggle:not(:disabled), .task-trigger-program-add:not(:disabled)';
function fieldInput(container: HTMLElement | null | undefined, field: string) {
  const target = Array.from(container?.querySelectorAll<HTMLElement>('[data-task-field]') ?? [])
    .find(element => element.dataset.taskField === field);
  return target?.matches(inputSelector) ? target : target?.querySelector<HTMLElement>(inputSelector);
}
function invalidActionField(action: Action) {
  const error = actionErrors.get(action);
  if (!error) return undefined;
  const field = error.field ?? actionValidationFields[action.type];
  if (field !== undefined) return field;
  if (action.type === 'TaskState') return action.taskName.trim() ? 'state' : 'taskName';
  if (!action.pluginId) return 'pluginId';
  if (!action.actionId) return 'actionId';
  const parameter = invalidParameter(actionDefinitionFor(action)?.parameters, action.parameters);
  return parameter ? `parameters.${parameter.key}` : 'actionId';
}
async function focusTaskError(currentEditor: Task, signal?: AbortSignal) {
  await nextTick();
  if (!isPageActive || signal?.aborted || editor.value !== currentEditor) return;
  for (const field of ['name', 'cron', 'event', 'processPaths', 'extension']) {
    if (!fieldErrors.value[field]) continue;
    const target = field === 'extension' ? invalidTriggerField.value ?? field : field;
    let input = fieldInput(editorForm.value, target);
    if (field === 'processPaths') {
      input = fieldInput(editorForm.value, `processPaths.${invalidProcessPath.value}`)
        ?? fieldInput(editorForm.value, 'addProcess');
    }
    if (input) { focusInvalidInput(input); return; }
  }
  for (const [index, action] of currentEditor.actions.entries()) {
    const field = invalidActionField(action);
    if (!field) continue;
    const container = editorForm.value?.querySelector<HTMLElement>(`[data-task-action="${index}"]`);
    const input = fieldInput(container, field) ?? fieldInput(container, 'type');
    if (input) {
      focusInvalidInput(input, field === 'keys' ? input.closest<HTMLElement>('.selected-keys') : undefined);
      return;
    }
  }
}
function clearTriggerErrors() {
  triggerErrorField.value = '';
  delete fieldErrors.value.cron;
  delete fieldErrors.value.event;
  delete fieldErrors.value.processPaths;
  delete fieldErrors.value.extension;
}
watch(() => editor.value?.name, () => { delete fieldErrors.value.name; }, { flush: 'sync' });
watch(() => JSON.stringify(editor.value?.trigger), clearTriggerErrors, { flush: 'sync' });
watch(
  () => editor.value?.actions.map(action => [action, JSON.stringify(actionPayload(action))] as const),
  current => {
    const snapshots = new Map<Action, string>();
    for (const [action, snapshot] of current ?? []) snapshots.set(toRaw(action), snapshot);
    for (const [action, error] of actionErrors)
      if (snapshots.get(toRaw(action)) !== error.snapshot) actionErrors.delete(action);
  },
);
function selectActionType(index: number, type: Action['type']) {
  if (!editor.value) return;
  editor.value.actions[index].type = type;
  if (type === 'Extension') editor.value.actions[index].parameters = {};
}
function actionDefinitionFor(action?: Action) {
  return registeredActions.value.find(item => item.pluginId === action?.pluginId && item.actionId === action?.actionId);
}
const pendingActionOptions = new Map<string, Promise<ActionOption[]>>();
const detailActionOptions = ref(new Map<string, ActionOption[]>());
function actionOptionsKey(pluginId: string, actionId: string, parameterKey: string) {
  return JSON.stringify([pluginId, actionId, parameterKey]);
}
function fetchActionOptions(pluginId: string, actionId: string, parameterKey: string): Promise<ActionOption[]> {
  const key = actionOptionsKey(pluginId, actionId, parameterKey);
  const pending = pendingActionOptions.get(key);
  if (pending) return pending;
  const request = (async () => {
    const options = await runPageRequest<unknown>(
      `/api/tasks/extensions/actions/${encodeURIComponent(pluginId)}/${encodeURIComponent(actionId)}/options/${encodeURIComponent(parameterKey)}`,
      {},
      response => {
        if (!response.ok) throw new Error('读取动作配置失败');
        return response.json();
      },
    );
    if (
      !Array.isArray(options) ||
      !options.every(
        (option) =>
          option &&
          typeof option.value === 'string' &&
          option.value.trim() &&
          typeof option.label === 'string' &&
          option.label.trim(),
      )
    )
      throw new Error('动作配置数据无效');
    return options as ActionOption[];
  })().finally(() => {
    if (pendingActionOptions.get(key) === request) pendingActionOptions.delete(key);
  });
  pendingActionOptions.set(key, request);
  return request;
}
watch([detailTarget, registeredActions], ([task], _, onCleanup) => {
  detailActionOptions.value = new Map();
  if (!task) return;
  let active = true;
  onCleanup(() => { active = false; });
  const requests = task.actions.flatMap(action => {
    const definition = action.type === 'Extension' ? actionDefinitionFor(action) : undefined;
    if (!definition?.available || !definition.dynamicOptions) return [];
    return definition.parameters
      .filter(parameter => parameter.type === 'Select' || parameter.type === 'MultiCheckbox')
      .map(async parameter => {
        const { pluginId, actionId } = definition;
        const options = await fetchActionOptions(pluginId, actionId, parameter.key);
        if (active && isPageActive) detailActionOptions.value.set(actionOptionsKey(pluginId, actionId, parameter.key), options);
      });
  });
  void Promise.all(requests).catch(() => { if (active && isPageActive) showToast('读取动作配置失败'); });
}, { flush: 'sync' });
async function loadActionOptions(index: number, parameterKey: string): Promise<boolean> {
  const currentEditor = editor.value;
  const action = currentEditor?.actions[index];
  if (action?.type !== 'Extension' || !action.pluginId || !action.actionId) return false;
  const pluginId = action.pluginId;
  const actionId = action.actionId;
  const definition = actionDefinitionFor(action);
  if (!definition?.available) return false;
  if (!definition?.dynamicOptions) return true;
  try {
    const options = await fetchActionOptions(pluginId, actionId, parameterKey);
    if (!isPageActive || editor.value !== currentEditor) return false;
    const current = currentEditor?.actions[index];
    if (current !== action || current?.pluginId !== pluginId || current.actionId !== actionId) return false;
    const parameter = definition.parameters.find((item) => item.key === parameterKey);
    if (!parameter) return false;
    parameter.options = options;
    return true;
  } catch {
    if (isPageActive && editor.value === currentEditor) showToast('读取动作配置失败');
    return false;
  }
}
function loadActionOptionsForAction(index: number) {
  const action = editor.value?.actions[index];
  const definition = actionDefinitionFor(action);
  if (!definition?.available || !definition.dynamicOptions) return;
  for (const parameter of definition.parameters)
    if (parameter.type === 'Select' || parameter.type === 'MultiCheckbox')
      void loadActionOptions(index, parameter.key);
}
function selectExtensionAction(index: number, actionId: string) {
  if (!editor.value) return;
  const action = editor.value.actions[index];
  const definition = actionDefinitionFor({ ...action, actionId });
  action.actionId = actionId;
  action.parameters = parameterDefaults(definition?.parameters);
  loadActionOptionsForAction(index);
}
const filteredTasks = computed(() => {
  const keyword = taskSearch.value.trim().toLocaleLowerCase();
  return keyword
    ? tasks.value.filter((task) => task.name.toLocaleLowerCase().includes(keyword))
    : tasks.value;
});
const triggerLabel = (trigger: Trigger, cronAvailable?: boolean) =>
  trigger.type === 'Cron'
    ? `Cron 计划 · ${trigger.cron || '未设置'}${cronAvailable === false ? '（不可用）' : ''}`
    : trigger.type === 'WinPilotStartup'
      ? 'WinPilot 启动'
      : trigger.type === 'Extension'
        ? extensionTriggerLabel(trigger)
        : `程序事件 · ${trigger.event === 'Started' ? '程序启动时' : trigger.event === 'Exited' ? '程序退出时' : '未选择事件'}`;
function extensionTriggerLabel(trigger: Trigger) {
  const definition = registeredTriggers.value.find(
    (item) => item.pluginId === trigger.pluginId && item.triggerId === trigger.triggerId,
  );
  const parameter = definition?.parameters.find(
    (item) =>
      item.type === 'Select' &&
      item.options?.some((option) => option.value === String(trigger.parameters?.[item.key] ?? '')),
  );
  const selected = parameter?.options?.find(
    (option) => option.value === String(trigger.parameters?.[parameter.key] ?? ''),
  );
  const pluginName = definition?.pluginDisplayName || trigger.pluginId || '未选择插件';
  const triggerName = definition?.triggerName || trigger.triggerId || '未选择配置';
  const unavailable = extensionsLoaded.value && trigger.pluginId && trigger.triggerId && (
    !definition?.available || definition.parameters?.some(parameter => hasUnavailableParameter(parameter, trigger.parameters?.[parameter.key]))
  );
  return `扩展 · ${pluginName} · ${triggerName}${selected ? ` - ${selected.label}` : ''}${unavailable ? '（不可用）' : ''}`;
}
function hasUnavailableTrigger(task: Task) {
  if (!extensionsLoaded.value || task.trigger.type !== 'Extension' || !task.trigger.pluginId || !task.trigger.triggerId) return false;
  const definition = registeredTriggers.value.find(
    (item) =>
      item.pluginId === task.trigger.pluginId && item.triggerId === task.trigger.triggerId,
  );
  return !definition?.available || Boolean(definition.parameters?.some(parameter =>
    hasUnavailableParameter(parameter, task.trigger.parameters?.[parameter.key]),
  ));
}
function taskWarningText(task: ListedTask) {
  const warnings: string[] = [];
  if (task.trigger.type === 'Cron' && task.cronAvailable === false) warnings.push('Cron 表达式没有后续执行时间');
  if (hasUnavailableTrigger(task)) warnings.push('插件触发条件不可用');
  if (task.actions.some(action => {
    if (!extensionsLoaded.value || action.type !== 'Extension') return false;
    const definition = actionDefinitionFor(action);
    if (!definition?.available) return true;
    return !definition.dynamicOptions && definition.parameters.some(parameter => hasUnavailableParameter(parameter, action.parameters?.[parameter.key]));
  })) warnings.push('部分插件动作不可用');
  return warnings.join('\n');
}
const actionCoreLabel = (action: Action) =>
  action.type === 'Extension'
    ? extensionActionLabel(action)
    : action.type === 'Delay'
      ? `延迟 ${action.delaySeconds || 0} 秒`
      : action.type === 'Shell'
        ? `Shell · ${action.shell} · ${action.command || '未设置命令'}`
        : action.type === 'Keyboard'
          ? `按键模拟 · ${orderKeys(action.keys).join(' + ') || '未设置'}`
          : action.type === 'StartTag'
            ? `启动快捷标签 · ${action.tagName || '未选择'}`
            : action.type === 'CloseProcess'
              ? `关闭进程 · ${action.processName || '未指定'}${action.currentUserOnly ? ' · 当前用户' : ''}`
              : action.type === 'SendNotification'
                ? `发送通知 · ${action.notificationContent || '未设置'}`
                : action.type === 'TaskState'
                  ? `任务状态 · ${taskStateOptions.find(option => option.value === action.state)?.label ?? '请选择状态'} · ${action.taskName || '未选择任务'}`
                  : `启动程序 · ${action.programPath || '未指定'}`;
const actionLabel = (action: Action, index: number, total: number) =>
  `${actionCoreLabel(action)}${action.continueOnFailure && index < total - 1 ? ' · 失败后继续' : ''}`;
function extensionActionLabel(action: Action) {
  const definition = actionDefinitionFor(action);
  if (!definition) return `扩展 · ${action.pluginId || '未选择插件'} · ${action.actionId || '未选择动作'}${extensionsLoaded.value && action.pluginId && action.actionId ? '（不可用）' : ''}`;
  if (!definition.available) return `${definition.actionName}（不可用）`;
  const labels = definition.parameters.flatMap((parameter) => {
    const options = definition.dynamicOptions
      ? detailActionOptions.value.get(actionOptionsKey(definition.pluginId, definition.actionId, parameter.key))
      : parameter.options;
    const value = action.parameters?.[parameter.key];
    const selectedValues =
      parameter.type === 'MultiCheckbox' && Array.isArray(value)
        ? value.map(String)
        : parameter.type === 'Select'
          ? [String(value ?? '')]
          : [];
    return selectedValues
      .filter(Boolean)
      .map(
        (selected) =>
          options?.find((option) => option.value === selected)?.label ??
          (definition.dynamicOptions && options === undefined ? selected : `${selected}（不可用）`),
      );
  });
  return `${definition.actionName}${labels.length ? ` - ${labels.join('、')}` : ''}`;
}
function openTaskEditor(task?: Task) {
  const savedTask = task ? JSON.parse(JSON.stringify(task)) as Task : undefined;
  editor.value = {
    name: savedTask?.name ?? '',
    enabled: savedTask?.enabled ?? true,
    trigger: { ...createTrigger(), ...savedTask?.trigger },
    actions: savedTask ? savedTask.actions.map(action => ({
      ...createAction(),
      ...action,
      ...(action.type === 'Keyboard' ? { keys: orderKeys(action.keys ?? []) } : {}),
    })) : [createAction()],
  };
  originalName.value = task?.name ?? '';
  detailTarget.value = undefined;
  fieldErrors.value = {};
  actionErrors.clear();
  pageError.value = '';
  editor.value?.actions.forEach((action, index) => {
    if (action.type === 'Extension') loadActionOptionsForAction(index);
  });
}
async function loadTasks() {
  loadController?.abort();
  const controller = new AbortController();
  loadController = controller;
  try {
    const result = await runPageRequest('/api/tasks', { signal: controller.signal }, async response => {
      if (!response.ok) throw new Error(await readApiError(response, '操作失败'));
      return response.json();
    });
    if (!isPageActive || loadController !== controller || controller.signal.aborted) return;
    tasks.value = result;
    tasksLoaded.value = true;
    pageError.value = '';
  } catch (cause) {
    if (isPageActive && loadController === controller && !controller.signal.aborted && !isAbortError(cause))
      pageError.value = userErrorMessage(cause, '加载任务失败');
  } finally {
    if (loadController === controller) loadController = undefined;
  }
}
async function loadExtensions() {
  extensionsController?.abort();
  const controller = new AbortController();
  extensionsController = controller;
  extensionsLoading.value = true;
  extensionsError.value = '';
  try {
    const [triggers, actions] = await Promise.all(['triggers', 'actions'].map(kind =>
      runPageRequest(`/api/tasks/extensions/${kind}`, { signal: controller.signal }, async response => {
        if (!response.ok) throw new Error(await readApiError(response, '无法读取插件扩展，请重试'));
        return response.json();
      })));
    if (!isPageActive || extensionsController !== controller || controller.signal.aborted) return;
    registeredTriggers.value = triggers;
    registeredActions.value = actions;
    extensionsLoaded.value = true;
    editor.value?.actions.forEach((action, index) => {
      if (action.type === 'Extension') loadActionOptionsForAction(index);
    });
  } catch (cause) {
    if (isPageActive && extensionsController === controller && !controller.signal.aborted && !isAbortError(cause))
      extensionsError.value = userErrorMessage(cause, '无法读取插件扩展，请重试');
  } finally {
    if (extensionsController === controller) {
      extensionsLoading.value = false;
      extensionsController = undefined;
    }
  }
}
const extensionPlugins = computed(() => [
  ...new Map(registeredTriggers.value.map((item) => [item.pluginId, item])).values(),
]);
const extensionTriggers = computed(() =>
  registeredTriggers.value.filter((item) => item.pluginId === editor.value?.trigger.pluginId),
);
function triggerDefinitionFor(triggerId?: string) {
  return extensionTriggers.value.find((item) => item.triggerId === triggerId);
}
function selectExtensionPlugin(pluginId: string) {
  if (!editor.value) return;
  editor.value.trigger.pluginId = pluginId;
  editor.value.trigger.triggerId = '';
  editor.value.trigger.parameters = {};
}
function selectExtensionTrigger(triggerId: string) {
  if (!editor.value) return;
  const definition = triggerDefinitionFor(triggerId);
  editor.value.trigger.triggerId = triggerId;
  editor.value.trigger.parameters = parameterDefaults(definition?.parameters);
}
function selectTriggerParameter(key: string, value: unknown) {
  if (!editor.value) return;
  editor.value.trigger.parameters ??= {};
  editor.value.trigger.parameters[key] = value;
}
const validate = (value: Task) => validateTask(value, registeredTriggers.value, registeredActions.value);
async function saveTask() {
  if (!editor.value || safeModeEnabled.value || pendingOperations.has('save')) return;
  const value = JSON.parse(JSON.stringify(editor.value)) as Task;
  value.name = value.name.trim();
  value.trigger.processPaths = value.trigger.processPaths.map((path) => path.trim()).filter(Boolean);
  for (const action of value.actions) if (action.type === 'Keyboard') action.keys = orderKeys(action.keys);
  if (value.actions.length) value.actions[value.actions.length - 1].continueOnFailure = false;
  const errors = validate(value);
  if (Object.keys(errors).length) {
    triggerErrorField.value = '';
    fieldErrors.value = Object.fromEntries(Object.entries(errors).filter(([key]) => !key.startsWith('action-')));
    actionErrors.clear();
    editor.value.actions.forEach((action, index) => {
      const message = errors[`action-${index}`];
      if (message) actionErrors.set(action, { message, snapshot: JSON.stringify(actionPayload(action)) });
    });
    showToast('请检查任务表单中的配置');
    await focusTaskError(editor.value);
    return;
  }
  const url = originalName.value ? `/api/tasks/${encodeURIComponent(originalName.value)}` : '/api/tasks';
  const currentEditor = editor.value;
  const submittedName = currentEditor.name;
  const submittedTrigger = JSON.stringify(currentEditor.trigger);
  const submittedActions = currentEditor.actions.map(action => ({ action, snapshot: JSON.stringify(actionPayload(action)) }));
  await performOperation('save', url, {
    method: originalName.value ? 'PUT' : 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      ...value,
      trigger: { ...value.trigger, event: value.trigger.type === 'Process' ? value.trigger.event : undefined },
      actions: value.actions.map(actionPayload),
    }),
  }, async () => {
    if (editor.value === currentEditor) editor.value = undefined;
    showToast('任务已保存', 'success');
    await loadTasks();
  }, async (response, signal) => {
    let problem: unknown;
    try { problem = await response.json(); }
    catch (cause) { if (isAbortError(cause)) throw cause; return '操作失败'; }
    const errors = problem && typeof problem === 'object' && !Array.isArray(problem)
      ? (problem as Record<string, unknown>).errors : undefined;
    let applied = false;
    if ((response.status === 400 || response.status === 409) && isPageActive && !signal.aborted && editor.value === currentEditor
      && errors && typeof errors === 'object' && !Array.isArray(errors)) {
      triggerErrorField.value = '';
      fieldErrors.value = {};
      actionErrors.clear();
      const triggerFields: Record<string, string> = {
        'trigger.cron': 'cron', 'trigger.event': 'event', 'trigger.processPaths': 'processPaths',
        'trigger.pluginId': 'extension', 'trigger.triggerId': 'extension', 'trigger.parameters': 'extension',
      };
      for (const [path, messages] of Object.entries(errors)) {
        if (!Array.isArray(messages)) continue;
        const message = messages.find(value => typeof value === 'string' && value.trim());
        if (!message) continue;
        const triggerField = triggerFields[path.split(/[.\[\]]/).slice(0, 2).join('.')];
        const actionIndex = path.match(/^actions\[(\d+)\](?:\.|$)/)?.[1];
        const submitted = actionIndex === undefined ? undefined : submittedActions[Number(actionIndex)];
        if (path === 'name' && currentEditor.name === submittedName) {
          fieldErrors.value.name = message;
          applied = true;
        } else if (triggerField && JSON.stringify(currentEditor.trigger) === submittedTrigger) {
          fieldErrors.value[triggerField] = message;
          if (triggerField === 'extension' && !triggerErrorField.value)
            triggerErrorField.value = path.replace(/^trigger\./, '');
          applied = true;
        } else if (submitted && currentEditor.actions.includes(submitted.action)
          && JSON.stringify(actionPayload(submitted.action)) === submitted.snapshot) {
          actionErrors.set(submitted.action, { message, snapshot: submitted.snapshot, field: path.replace(/^actions\[\d+\]\.?/, '') });
          applied = true;
        }
      }
    }
    if (applied) await focusTaskError(currentEditor, signal);
    return applied ? '请检查任务表单中的配置' : apiErrorMessage(problem, '操作失败');
  });
}
async function deleteTask() {
  const target = deleteTarget.value;
  if (!target || safeModeEnabled.value) return;
  await performOperation('delete', `/api/tasks/${encodeURIComponent(target.name)}`, { method: 'DELETE' }, async () => {
    if (deleteTarget.value === target) deleteTarget.value = undefined;
    await loadTasks();
  });
}
async function toggleTaskEnabled(task: Task, event?: Event) {
  if (event && event.target instanceof HTMLInputElement) event.target.checked = task.enabled;
  await performOperation(`toggle:${task.name}`, `/api/tasks/${encodeURIComponent(task.name)}/enabled`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ enabled: !task.enabled }),
  }, loadTasks);
}

function requestRun(task: Task) {
  runTarget.value = task;
  detailTarget.value = undefined;
}
async function runTask() {
  const target = runTarget.value;
  if (!target) return;
  await performOperation('run', `/api/tasks/${encodeURIComponent(target.name)}/run`, { method: 'POST' }, () => {
    if (runTarget.value === target) runTarget.value = undefined;
  });
}
function move(items: unknown[], index: number, delta: number) {
  const next = index + delta;
  if (next < 0 || next >= items.length) return;
  const [item] = items.splice(index, 1);
  items.splice(next, 0, item);
}
async function openVariableHelp() {
  variableHelpController?.abort();
  const controller = new AbortController();
  variableHelpController = controller;
  variableHelpOpen.value = true;
  variableHelpLoading.value = true;
  variableHelpError.value = '';
  try {
    const variables = await runPageRequest('/api/tasks/variables', { signal: controller.signal }, response => {
      if (!response.ok) throw new Error('无法读取变量');
      return response.json();
    });
    if (isPageActive && variableHelpOpen.value && variableHelpController === controller && !controller.signal.aborted)
      registeredVariables.value = variables;
  } catch (cause) {
    if (!isPageActive || controller.signal.aborted || variableHelpController !== controller || isAbortError(cause)) return;
    if (cause instanceof WinPilotConnectionError) variableHelpOpen.value = false;
    else variableHelpError.value = cause instanceof Error ? cause.message : '无法读取变量';
  } finally {
    if (variableHelpController === controller) {
      variableHelpLoading.value = false;
      variableHelpController = undefined;
    }
  }
}
onMounted(() => {
  void loadTasks();
  void loadExtensions();
});
watch(variableHelpOpen, (open) => {
  if (!open) variableHelpController?.abort();
}, { flush: 'sync' });
watch(safeModeEnabled, (enabled) => {
  if (enabled) {
    editor.value = undefined;
    deleteTarget.value = undefined;
    for (const [key, controller] of pendingOperations) if (key === 'save' || key === 'delete') controller.abort();
  }
});
onBeforeUnmount(() => {
  isPageActive = false;
  loadController?.abort();
  extensionsController?.abort();
  variableHelpController?.abort();
  for (const controller of requestControllers) controller.abort();
  for (const controller of pendingOperations.values()) controller.abort();
  pendingOperations.clear();
  pendingActionOptions.clear();
  pendingActionResources.clear();
});
</script>
<template>
  <section class="tasks-view">
    <p v-if="pageError || extensionsError" class="status-message error">
      {{ pageError || extensionsError }}
      <button v-if="!pageError && extensionsError" class="button button-secondary" type="button" :disabled="extensionsLoading" @click="loadExtensions">重试</button>
    </p>
    <section class="program-panel task-list">
      <div class="program-panel-header">
        <div class="program-actions">
          <label v-if="tasks.length" class="tag-search">
            <input v-model="taskSearch" type="search" placeholder="搜索任务名" />
          </label>
          <button
            class="button button-primary"
            :class="{ 'safe-mode-button': safeModeEnabled }"
            type="button"
            :disabled="safeModeEnabled"
            :title="safeModeEnabled ? safeModeMessage : undefined"
            @click="openTaskEditor()"
          >
            <AppIcon v-if="safeModeEnabled" name="lock" />
            ＋ 创建任务
          </button>
        </div>
      </div>
      <article v-for="task in filteredTasks" :key="task.name" class="task-row" @click="detailTarget = task">
        <div class="task-row-main">
          <button class="task-name-button" type="button">
            {{ task.name }}
          </button>
        </div>
        <div class="task-row-controls">
          <span v-if="taskWarningText(task)" class="task-warning" role="img" :title="taskWarningText(task)" :aria-label="taskWarningText(task)">
            <AppIcon name="alert-circle" />
          </span>
          <label class="task-row-toggle" @click.stop>
            <input type="checkbox" :checked="task.enabled" :disabled="pendingOperations.has(`toggle:${task.name}`)" @change="toggleTaskEnabled(task, $event)" />
          </label>
        </div>
      </article>
      <p v-if="tasksLoaded && !tasks.length && !pageError" class="program-empty">暂无任务，点击“创建任务”开始</p>
    </section>
    <AppModal
      v-if="editor"
      :dismissible="!pendingOperations.has('save')"
      :title="originalName ? '编辑任务' : '创建任务'"
      :dismiss-on-backdrop="false"
      @close="editor = undefined"
    >
      <div ref="editorForm" class="task-form">
        <label class="field-control" data-task-field="name">
          <span>任务名称 *</span>
          <input v-model="editor.name" :aria-invalid="Boolean(fieldErrors.name) || undefined" />
          <small v-if="fieldErrors.name" class="field-error">{{ fieldErrors.name }}</small>
        </label>
        <label class="toggle-control">
          <input v-model="editor.enabled" type="checkbox" />
          <span>启用任务</span>
        </label>
        <div class="task-form-section">
          <div class="task-section-heading">
            <div>
              <h3>触发条件</h3>
              <p>选择任务执行的时机</p>
            </div>
          </div>
          <label class="field-control">
            <span>类型</span>
            <AppSelect
              v-model="editor.trigger.type"
              :options="[
                { value: 'Cron', label: 'Cron 计划' },
                { value: 'Process', label: '程序事件' },
                { value: 'WinPilotStartup', label: 'WinPilot 启动' },
                { value: 'Extension', label: '插件扩展' },
              ]"
            />
          </label>
          <CronEditor
            data-task-field="cron"
            v-if="editor.trigger.type === 'Cron'"
            v-model="editor.trigger.cron"
            v-model:error="fieldErrors.cron"
          />
          <div v-else-if="editor.trigger.type === 'Process'" class="task-trigger-process">
            <label class="field-control" data-task-field="event">
              <span>事件 *</span>
              <AppSelect v-model="editor.trigger.event" :invalid="Boolean(fieldErrors.event)" :options="[
                { value: '', label: '请选择事件' },
                { value: 'Started', label: '程序启动时' },
                { value: 'Exited', label: '程序退出时' },
              ]" />
              <small v-if="fieldErrors.event" class="field-error">{{ fieldErrors.event }}</small>
            </label>
            <div v-if="editor.trigger.event" class="task-trigger-programs" data-task-field="processPaths">
              <div
                v-for="(_, index) in editor.trigger.processPaths"
                :key="index"
                :data-task-field="`processPaths.${index}`"
                class="field-control"
              >
                <div class="input-action">
                  <input
                    v-model="editor.trigger.processPaths[index]"
                    :aria-invalid="Boolean(fieldErrors.processPaths && index === invalidProcessPath) || undefined"
                    aria-label="程序绝对路径"
                    placeholder="程序绝对路径，例如：C:\Program Files\App\App.exe"
                  />
                  <button
                    class="input-action-button"
                    type="button"
                    title="删除"
                    aria-label="删除"
                    @click="editor.trigger.processPaths.splice(index, 1)"
                  >
                    <AppIcon name="close" />
                  </button>
                </div>
              </div>
              <div class="task-trigger-program-controls">
                <button
                  class="button button-secondary task-trigger-program-add"
                  data-task-field="addProcess"
                  :aria-invalid="Boolean(fieldErrors.processPaths && !editor.trigger.processPaths.length) || undefined"
                  type="button"
                  @click="editor.trigger.processPaths.push('')"
                >
                  ＋ 添加程序
                </button>
                <p class="field-hint">任一程序发生所选事件时触发任务</p>
              </div>
            </div>
            <small v-if="editor.trigger.event && fieldErrors.processPaths" class="field-error">{{ fieldErrors.processPaths }}</small>
          </div>
          <div v-else-if="editor.trigger.type === 'Extension'" class="task-trigger-extension">
            <label class="field-control" data-task-field="pluginId">
              <span>插件 *</span>
              <AppSelect
                :model-value="editor.trigger.pluginId || ''"
                :disabled="!extensionsLoaded"
                :invalid="invalidTriggerField === 'pluginId'"
                :options="[
                  { value: '', label: '请选择插件' },
                  ...extensionPlugins.map((plugin) => ({
                    value: plugin.pluginId,
                    label: plugin.pluginDisplayName,
                  })),
                  ...(editor.trigger.pluginId &&
                  !extensionPlugins.some((item) => item.pluginId === editor?.trigger.pluginId)
                    ? [{ value: editor.trigger.pluginId, label: `${editor.trigger.pluginId}${extensionsLoaded ? '（不可用）' : ''}` }]
                    : []),
                ]"
                @change="selectExtensionPlugin($event)"
              />
            </label>
            <label class="field-control" data-task-field="triggerId">
              <span>触发条件 *</span>
              <AppSelect
                :model-value="editor.trigger.triggerId || ''"
                :disabled="!extensionsLoaded"
                :invalid="invalidTriggerField === 'triggerId'"
                :options="[
                  { value: '', label: '请选择触发条件' },
                  ...extensionTriggers.map((item) => ({ value: item.triggerId, label: item.triggerName })),
                  ...(editor.trigger.triggerId &&
                  !extensionTriggers.some((item) => item.triggerId === editor?.trigger.triggerId)
                    ? [{ value: editor.trigger.triggerId, label: `${editor.trigger.triggerId}${extensionsLoaded ? '（不可用）' : ''}` }]
                    : []),
                ]"
                @change="selectExtensionTrigger($event)"
              />
            </label>
            <template v-if="triggerDefinitionFor(editor.trigger.triggerId)?.parameters?.length">
              <label
                v-for="parameter in triggerDefinitionFor(editor.trigger.triggerId)?.parameters"
                :key="parameter.key"
                :data-task-field="`parameters.${parameter.key}`"
                class="field-control"
              >
                <span>{{ parameter.label }}{{ parameter.required ? ' *' : '' }}</span>
                <AppSelect
                  v-if="parameter.type === 'Select'"
                  :invalid="invalidTriggerField === `parameters.${parameter.key}`"
                  :model-value="editor.trigger.parameters?.[parameter.key] ?? ''"
                  :options="[
                    { value: '', label: '请选择配置' },
                    ...(parameter.options || []),
                    ...(unavailableSelectValue(parameter, editor.trigger.parameters?.[parameter.key])
                      ? [{ value: unavailableSelectValue(parameter, editor.trigger.parameters?.[parameter.key]), label: `${unavailableSelectValue(parameter, editor.trigger.parameters?.[parameter.key])}（不可用）` }]
                      : []),
                  ]"
                  @change="selectTriggerParameter(parameter.key, $event)"
                />
                <AppMultiSelect
                  v-else-if="parameter.type === 'MultiCheckbox'"
                  :invalid="invalidTriggerField === `parameters.${parameter.key}`"
                  :model-value="editor.trigger.parameters?.[parameter.key] ?? []"
                  :options="multiParameterOptions(parameter, editor.trigger.parameters?.[parameter.key])"
                  :label="multiSelectionLabel(editor.trigger.parameters?.[parameter.key])"
                  @toggle="(value, checked) => { if (editor) toggleMultiOption(editor.trigger, parameter.key, value, checked); }"
                />
                <input
                  v-else-if="parameter.type === 'Checkbox'"
                  :aria-invalid="invalidTriggerField === `parameters.${parameter.key}` || undefined"
                  type="checkbox"
                  :checked="Boolean(editor.trigger.parameters?.[parameter.key])"
                  @change="selectTriggerParameter(parameter.key, ($event.target as HTMLInputElement).checked)"
                />
                <textarea
                  v-else-if="parameter.type === 'Textarea'"
                  :aria-invalid="invalidTriggerField === `parameters.${parameter.key}` || undefined"
                  :value="editor.trigger.parameters?.[parameter.key] ?? ''"
                  @input="selectTriggerParameter(parameter.key, ($event.target as HTMLTextAreaElement).value)"
                />
                <input
                  v-else
                  :aria-invalid="invalidTriggerField === `parameters.${parameter.key}` || undefined"
                  :type="parameter.type === 'Number' ? 'number' : 'text'"
                  :value="editor.trigger.parameters?.[parameter.key] ?? ''"
                  @input="selectTriggerParameter(parameter.key, parameter.type === 'Number' ? ($event.target as HTMLInputElement).valueAsNumber : ($event.target as HTMLInputElement).value)"
                />
              </label>
            </template>
            <small v-if="fieldErrors.extension" class="field-error">{{ fieldErrors.extension }}</small>
          </div>
        </div>
        <div class="task-form-section">
          <div class="task-section-heading">
            <div>
              <h3>执行动作</h3>
              <p>动作按列表顺序执行</p>
            </div>
            <button class="button button-secondary" type="button" @click="editor.actions.push(createAction())">
              ＋ 添加动作
            </button>
          </div>
          <article v-for="(action, index) in editor.actions" :key="actionKey(action)" :data-task-action="index" class="task-config-card">
            <div class="task-config-heading">
              <strong>动作 {{ index + 1 }}</strong>
              <div>
                <button
                  class="icon-button"
                  type="button"
                  :disabled="index === 0"
                  @click="move(editor.actions, index, -1)"
                >
                  上移
                </button>
                <button
                  class="icon-button"
                  type="button"
                  :disabled="index === editor.actions.length - 1"
                  @click="move(editor.actions, index, 1)"
                >
                  下移
                </button>
                <button
                  class="icon-button task-action-remove"
                  type="button"
                  :disabled="editor.actions.length === 1"
                  @click="editor.actions.splice(index, 1)"
                >
                  删除
                </button>
              </div>
            </div>
            <TaskActionEditor
              :action="action"
              :tasks="tasks"
              :tags="tags"
              :registered-actions="registeredActions"
              :extensions-loaded="extensionsLoaded"
              :resource-state="actionResource(action.type)"
              :load-resource="() => loadActionResource(action.type)"
              :channel-options="channelOptions"
              :error="actionErrors.get(action)?.message"
              :invalid-field="invalidActionField(action)"
              :load-options="parameterKey => loadActionOptions(index, parameterKey)"
              @select-type="selectActionType(index, $event)"
              @select-extension-action="selectExtensionAction(index, $event)"
              @show-variables="openVariableHelp"
            />
            <label v-if="index < editor.actions.length - 1" class="toggle-control">
              <input v-model="action.continueOnFailure" type="checkbox" />
              <span>动作失败后继续执行</span>
            </label>
          </article>
        </div>
      </div>
      <template #actions>
        <button class="button button-secondary" type="button" :disabled="pendingOperations.has('save')" @click="editor = undefined">取消</button>
        <button class="button button-primary" type="button" :disabled="pendingOperations.has('save')" @click="saveTask">保存</button>
      </template>
    </AppModal>
    <TaskVariableHelp
      v-if="variableHelpOpen"
      :variables="registeredVariables"
      :loading="variableHelpLoading"
      :error="variableHelpError"
      @close="variableHelpOpen = false"
    />
    <AppModal v-if="detailTarget" :title="detailTarget.name" @close="detailTarget = undefined">
      <template v-if="taskWarningText(detailTarget)" #title-extra>
        <span class="task-warning" role="img" :title="taskWarningText(detailTarget)" :aria-label="taskWarningText(detailTarget)">
          <AppIcon name="alert-circle" />
        </span>
      </template>
      <p>{{ triggerLabel(detailTarget.trigger, detailTarget.cronAvailable) }}</p>
      <p v-for="(action, index) in detailTarget.actions" :key="index">
        {{ index + 1 }}. {{ actionLabel(action, index, detailTarget.actions.length) }}
      </p>
      <template #actions>
        <button
          class="button button-secondary"
          :class="{ 'safe-mode-button': safeModeEnabled }"
          :disabled="safeModeEnabled"
          :title="safeModeEnabled ? safeModeMessage : undefined"
          @click="openTaskEditor(detailTarget)"
        >
          <AppIcon v-if="safeModeEnabled" name="lock" />
          编辑
        </button>
        <button
          class="button button-danger"
          :class="{ 'safe-mode-button': safeModeEnabled }"
          :disabled="safeModeEnabled"
          :title="safeModeEnabled ? safeModeMessage : undefined"
          @click="
            deleteTarget = detailTarget;
            detailTarget = undefined;
          "
        >
          <AppIcon v-if="safeModeEnabled" name="lock" />
          删除
        </button>
        <button class="button button-primary" @click="requestRun(detailTarget)">立即运行</button>
      </template>
    </AppModal>
    <AppModal :dismissible="!pendingOperations.has('run')" v-if="runTarget" title="确认运行任务" @close="runTarget = undefined">
      <p>确认立即运行任务“{{ runTarget.name }}”吗？</p>
      <template #actions>
        <button class="button button-secondary" type="button" :disabled="pendingOperations.has('run')" @click="runTarget = undefined">取消</button>
        <button class="button button-primary" :disabled="pendingOperations.has('run')" @click="runTask">运行</button>
      </template>
    </AppModal>
    <AppModal :dismissible="!pendingOperations.has('delete')" v-if="deleteTarget" title="确认删除任务" @close="deleteTarget = undefined">
      <p>确认删除任务“{{ deleteTarget.name }}”吗？</p>
      <template #actions>
        <button class="button button-secondary" type="button" :disabled="pendingOperations.has('delete')" @click="deleteTarget = undefined">取消</button>
        <button class="button button-danger" :disabled="pendingOperations.has('delete')" @click="deleteTask">删除</button>
      </template>
    </AppModal>
  </section>
</template>
