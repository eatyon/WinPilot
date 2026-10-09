<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowReactive, watch } from 'vue';
import AppIcon from '../../components/AppIcon.vue';
import AppModal from '../../components/AppModal.vue';
import AppSelect from '../../components/AppSelect.vue';
import AppMultiSelect from '../../components/AppMultiSelect.vue';
import KeyboardKeyPicker from '../desktop/KeyboardKeyPicker.vue';
import CronEditor from './CronEditor.vue';
import { apiFetch, isAbortError, userErrorMessage, WinPilotConnectionError } from '../../api/client';
import { showToast } from '../../ui/toast';
import { safeModeEnabled, safeModeMessage } from '../security/safeMode';
type Trigger = {
  type: 'Cron' | 'WinPilotStartup' | 'ProcessStarted' | 'ProcessExited' | 'Extension';
  cron: string;
  processPaths: string[];
  pluginId?: string;
  triggerId?: string;
  parameters?: Record<string, any>;
};
type RegisteredTrigger = {
  pluginId: string;
  pluginDisplayName: string;
  triggerId: string;
  triggerName: string;
  available: boolean;
  parameters: ParameterDefinition[];
};
type ParameterDefinition = {
  key: string;
  label: string;
  type: 'Text' | 'Number' | 'Select' | 'Checkbox' | 'Textarea' | 'MultiCheckbox';
  required: boolean;
  defaultValue?: unknown;
  options?: { value: string; label: string }[];
};
type RegisteredAction = {
  pluginId: string;
  pluginDisplayName: string;
  actionId: string;
  actionName: string;
  available: boolean;
  dynamicOptions?: boolean;
  parameters: ParameterDefinition[];
};
type RegisteredVariable = {
  name: string;
  displayName: string;
  description: string;
  type: 'Text' | 'Image';
  pluginId: string;
  pluginDisplayName: string;
};
type Action = {
  type:
    | 'StartProgram'
    | 'StartTag'
    | 'CloseProcess'
    | 'Delay'
    | 'Keyboard'
    | 'Shell'
    | 'SendNotification'
    | 'SendNotificationToChannel'
    | 'Extension';
  programPath: string;
  arguments: string;
  workingDirectory: string;
  runElevated: boolean;
  silent: boolean;
  tagName: string;
  processName: string;
  force: boolean;
  currentUserOnly: boolean;
  delaySeconds: number | '';
  keys: string[];
  shell: 'CMD' | 'PowerShell';
  command: string;
  notificationTitle: string;
  notificationContent: string;
  notificationChannel: string;
  pluginId?: string;
  actionId?: string;
  parameters?: Record<string, any>;
  continueOnFailure: boolean;
};
type Task = {
  name: string;
  enabled: boolean;
  trigger: Trigger;
  actions: Action[];
};
const tasks = ref<Task[]>([]),
  tags = ref<{ name: string }[]>([]),
  registeredTriggers = ref<RegisteredTrigger[]>([]),
  registeredActions = ref<RegisteredAction[]>([]),
  registeredVariables = ref<RegisteredVariable[]>([]),
  editor = ref<Task>(),
  originalName = ref(''),
  detailTarget = ref<Task>(),
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
let variableHelpController: AbortController | undefined;
const requestControllers = new Set<AbortController>();
const pendingOperations = shallowReactive(new Map<string, AbortController>());

async function fetchPageResource(url: string, init?: RequestInit): Promise<Response> {
  const controller = new AbortController();
  if (!isPageActive) controller.abort();
  requestControllers.add(controller);
  try {
    return await apiFetch(url, { ...init, signal: controller.signal });
  } finally {
    requestControllers.delete(controller);
  }
}

async function responseError(response: Response) {
  const problem = await response.json().catch(() => null) as { detail?: string } | null;
  return problem?.detail || '操作失败';
}

async function performOperation(key: string, url: string, init: RequestInit, success: () => Promise<void> | void) {
  if (!isPageActive || pendingOperations.has(key)) return;
  const controller = new AbortController();
  pendingOperations.set(key, controller);
  try {
    const response = await apiFetch(url, { ...init, signal: controller.signal });
    if (!response.ok) throw new Error(await responseError(response));
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

function parameterDefaults(parameters: ParameterDefinition[] = []) {
  return Object.fromEntries(parameters.map(parameter => [
    parameter.key,
    parameter.defaultValue == null
      ? (parameter.type === 'MultiCheckbox' ? [] : parameter.type === 'Checkbox' ? false : '')
      : JSON.parse(JSON.stringify(parameter.defaultValue)),
  ]));
}

const actionOptions = [
  ['StartProgram', '启动'],
  ['StartTag', '快捷标签'],
  ['CloseProcess', '关闭进程'],
  ['Delay', '延迟'],
  ['Shell', 'Shell'],
  ['Keyboard', '键盘模拟'],
  ['SendNotification', '发送通知'],
  ['SendNotificationToChannel', '发送通知到指定渠道'],
  ['Extension', '插件扩展'],
] as const;
const channelOptions = [
  ['Windows', 'Windows 通知'],
  ['Webhook', 'Webhook'],
  ['Telegram', 'Telegram'],
  ['WorkWeixin', '企业微信'],
  ['Feishu', '飞书'],
  ['OneBot', 'OneBot'],
  ['QQBot', 'QQBot'],
] as const;
function clearTriggerErrors() {
  delete fieldErrors.value.cron;
  delete fieldErrors.value.processPaths;
  delete fieldErrors.value.extension;
}
function clearActionError(index: number) {
  delete fieldErrors.value[`action-${index}`];
}
watch(
  () => editor.value?.actions.map((action) => action.pluginId),
  (current, previous) => {
    current?.forEach((pluginId, index) => {
      if (pluginId !== previous?.[index]) clearActionError(index);
    });
  },
);
function selectActionType(index: number, type: Action['type']) {
  if (!editor.value) return;
  editor.value.actions[index].type = type;
  if (type === 'Extension') editor.value.actions[index].parameters = {};
  clearActionError(index);
}
function actionDefinitionsFor(pluginId?: string) {
  return registeredActions.value.filter((item) => item.pluginId === pluginId);
}
function actionDefinitionFor(action?: Action) {
  return registeredActions.value.find(item => item.pluginId === action?.pluginId && item.actionId === action?.actionId);
}
type ActionOption = { value: string; label: string };
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
    const response = await fetchPageResource(
      `/api/plugin-task-extensions/actions/${encodeURIComponent(pluginId)}/${encodeURIComponent(actionId)}/options/${encodeURIComponent(parameterKey)}`,
    );
    if (!response.ok) throw new Error('读取动作配置失败');
    const options = (await response.json()) as unknown;
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
  })().finally(() => pendingActionOptions.delete(key));
  pendingActionOptions.set(key, request);
  return request;
}
watch(detailTarget, (task, _, onCleanup) => {
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
  clearActionError(index);
  loadActionOptionsForAction(index);
}
function multiSelectionLabel(value: unknown) {
  if (!Array.isArray(value) || value.length === 0) return '请选择配置';
  return `已选择 ${value.length} 项`;
}
function multiParameterOptions(parameter: ParameterDefinition, value: unknown) {
  const available = (parameter.options ?? []).map((option) => ({ ...option, unavailable: false }));
  const known = new Set(available.map((option) => option.value));
  const missing = Array.isArray(value)
    ? value
        .map(String)
        .filter((item) => !known.has(item))
        .map((item) => ({ value: item, label: `${item}（不可用）`, unavailable: true }))
    : [];
  return [...available, ...missing];
}
function unavailableSelectValue(parameter: ParameterDefinition, value: unknown) {
  const selected = String(value ?? '');
  return selected && !parameter.options?.some((option) => option.value === selected) ? selected : '';
}
function toggleMultiOption(action: Pick<Action, 'parameters'>, parameterKey: string, value: string, checked: boolean) {
  action.parameters ??= {};
  const selected = Array.isArray(action.parameters[parameterKey])
    ? (action.parameters[parameterKey] as string[])
    : [];
  const position = selected.indexOf(value);
  if (!checked && position >= 0) selected.splice(position, 1);
  else if (checked && position < 0) selected.push(value);
  action.parameters[parameterKey] = selected;
}
const filteredTasks = computed(() => {
  const keyword = taskSearch.value.trim().toLocaleLowerCase();
  return keyword
    ? tasks.value.filter((task) => task.name.toLocaleLowerCase().includes(keyword))
    : tasks.value;
});
const createTrigger = (): Trigger => ({
  type: 'Cron',
  cron: '',
  processPaths: [],
  pluginId: '',
  triggerId: '',
  parameters: {},
});
const createAction = (): Action => ({
  type: 'StartProgram',
  programPath: '',
  arguments: '',
  workingDirectory: '',
  runElevated: false,
  silent: false,
  tagName: '',
  processName: '',
  force: false,
  currentUserOnly: false,
  delaySeconds: '',
  keys: [],
  shell: 'PowerShell',
  command: '',
  notificationTitle: '',
  notificationContent: '',
  notificationChannel: 'Windows',
  pluginId: '',
  actionId: '',
  parameters: {},
  continueOnFailure: false,
});
const triggerLabel = (trigger: Trigger) =>
  trigger.type === 'Cron'
    ? `按计划执行 · ${trigger.cron || '未设置'}`
    : trigger.type === 'WinPilotStartup'
      ? 'WinPilot 启动时'
      : trigger.type === 'Extension'
        ? extensionTriggerLabel(trigger)
        : `${trigger.type === 'ProcessStarted' ? '当以下任意一个程序启动时' : '当以下任意一个程序关闭时'} · ${trigger.processPaths.join('、') || '未指定'}`;
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
  const unavailable = trigger.pluginId && trigger.triggerId && (
    !definition?.available || definition.parameters?.some(parameter => hasUnavailableParameter(parameter, trigger.parameters?.[parameter.key]))
  );
  return `扩展 · ${pluginName} · ${triggerName}${selected ? ` - ${selected.label}` : ''}${unavailable ? '（不可用）' : ''}`;
}
function hasUnavailableTrigger(task: Task) {
  if (task.trigger.type !== 'Extension' || !task.trigger.pluginId || !task.trigger.triggerId) return false;
  const definition = registeredTriggers.value.find(
    (item) =>
      item.pluginId === task.trigger.pluginId && item.triggerId === task.trigger.triggerId,
  );
  return !definition?.available || Boolean(definition.parameters?.some(parameter =>
    hasUnavailableParameter(parameter, task.trigger.parameters?.[parameter.key]),
  ));
}
function taskWarningText(task: Task) {
  const warnings: string[] = [];
  if (hasUnavailableTrigger(task)) warnings.push('插件触发条件不可用');
  if (task.actions.some(action => {
    if (action.type !== 'Extension') return false;
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
          ? `键盘模拟 · ${action.keys.join(' + ') || '未设置'}`
          : action.type === 'StartTag'
            ? `启动快捷标签 · ${action.tagName || '未选择'}`
            : action.type === 'CloseProcess'
              ? `关闭进程 · ${action.processName || '未指定'}${action.currentUserOnly ? ' · 当前用户' : ''}`
              : action.type === 'SendNotification' || action.type === 'SendNotificationToChannel'
                ? `发送通知 · ${action.notificationContent || '未设置'}`
                : `启动程序 · ${action.programPath || '未指定'}`;
const actionLabel = (action: Action, index: number, total: number) =>
  `${actionCoreLabel(action)}${action.continueOnFailure && index < total - 1 ? ' · 失败后继续' : ''}`;
function extensionActionLabel(action: Action) {
  const definition = actionDefinitionFor(action);
  if (!definition) return `扩展 · ${action.pluginId || '未选择插件'} · ${action.actionId || '未选择动作'}${action.pluginId && action.actionId ? '（不可用）' : ''}`;
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
    actions: savedTask ? savedTask.actions.map(action => ({ ...createAction(), ...action })) : [createAction()],
  };
  originalName.value = task?.name ?? '';
  detailTarget.value = undefined;
  fieldErrors.value = {};
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
    const urls = ['/api/tasks', '/api/programs/tags', '/api/plugin-task-extensions/triggers', '/api/plugin-task-extensions/actions'];
    const values = await Promise.all(urls.map(async url => {
      const response = await apiFetch(url, { signal: controller.signal });
      if (!response.ok) throw new Error(await responseError(response));
      return response.json();
    }));
    if (!isPageActive || loadController !== controller || controller.signal.aborted) return;
    [tasks.value, tags.value, registeredTriggers.value, registeredActions.value] = values;
    pageError.value = '';
  } catch (cause) {
    if (isPageActive && loadController === controller && !controller.signal.aborted && !isAbortError(cause))
      pageError.value = userErrorMessage(cause, '加载任务失败');
  } finally {
    if (loadController === controller) loadController = undefined;
  }
}
const extensionPlugins = computed(() => [
  ...new Map(registeredTriggers.value.map((item) => [item.pluginId, item])).values(),
]);
const actionPlugins = computed(() => [
  ...new Map(registeredActions.value.map((item) => [item.pluginId, item])).values(),
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
  clearTriggerErrors();
}
function selectExtensionTrigger(triggerId: string) {
  if (!editor.value) return;
  const definition = triggerDefinitionFor(triggerId);
  editor.value.trigger.triggerId = triggerId;
  editor.value.trigger.parameters = parameterDefaults(definition?.parameters);
  clearTriggerErrors();
}
function selectTriggerParameter(key: string, value: unknown) {
  if (!editor.value) return;
  editor.value.trigger.parameters ??= {};
  editor.value.trigger.parameters[key] = value;
  clearTriggerErrors();
}
function isRequiredParameterMissing(parameter: ParameterDefinition, value: unknown) {
  if (!parameter.required) return false;
  if (parameter.type === 'MultiCheckbox') return !Array.isArray(value) || value.length === 0;
  if (parameter.type === 'Checkbox') return false;
  return value === null || value === undefined || !String(value).trim();
}
function hasUnavailableParameter(parameter: ParameterDefinition, value: unknown) {
  if (parameter.type === 'Select') return Boolean(unavailableSelectValue(parameter, value));
  if (parameter.type !== 'MultiCheckbox' || !Array.isArray(value)) return false;
  return value
    .map(String)
    .some((selected) => !parameter.options?.some((option) => option.value === selected));
}
function parameterValidationError(definitions: ParameterDefinition[] = [], values?: Record<string, unknown>) {
  const missing = definitions.find(parameter => isRequiredParameterMissing(parameter, values?.[parameter.key]));
  if (missing)
    return missing.type === 'Select' || missing.type === 'MultiCheckbox'
      ? `请选择${missing.label}`
      : `请输入${missing.label}`;
  const unavailable = definitions.find(parameter => hasUnavailableParameter(parameter, values?.[parameter.key]));
  return unavailable ? `${unavailable.label}包含不可用配置，请重新选择` : '';
}
function validate(value: Task) {
  const result: Record<string, string> = {};
  if (!value.name.trim()) result.name = '请输入任务名称';
  const trigger = value.trigger;
  if (trigger.type === 'Cron' && !trigger.cron.trim()) result.cron = '请输入 Cron 表达式';
  if (trigger.type === 'ProcessStarted' || trigger.type === 'ProcessExited') {
    const paths = trigger.processPaths.map((path) => path.trim()).filter(Boolean);
    if (!paths.length) result.processPaths = '请至少选择一个程序绝对路径';
    else if (paths.some((path) => !/^(?:[a-zA-Z]:[\\/]|\\\\)/.test(path)))
      result.processPaths = '程序路径必须是绝对路径';
  }
  if (trigger.type === 'Extension' && (!trigger.pluginId || !trigger.triggerId))
    result.extension = '请选择插件和触发条件';
  if (trigger.type === 'Extension' && trigger.pluginId && trigger.triggerId) {
    const definition = registeredTriggers.value.find(
      (item) => item.pluginId === trigger.pluginId && item.triggerId === trigger.triggerId,
    );
    const error = parameterValidationError(definition?.parameters, trigger.parameters);
    if (error) result.extension = error;
  }
  value.actions.forEach((action, index) => {
    if (action.type === 'Extension' && (!action.pluginId || !action.actionId))
      result[`action-${index}`] = '请选择插件和动作';
    if (action.type === 'Extension' && action.pluginId && action.actionId) {
      const definition = actionDefinitionsFor(action.pluginId).find(
        (item) => item.actionId === action.actionId,
      );
      const error = parameterValidationError(definition?.parameters, action.parameters);
      if (error) result[`action-${index}`] = error;
    }
    if (action.type === 'StartProgram' && !action.programPath.trim())
      result[`action-${index}`] = '请选择要启动的程序';
    if (action.type === 'StartTag' && !action.tagName.trim())
      result[`action-${index}`] = '请选择快捷标签';
    if (action.type === 'CloseProcess' && !action.processName.trim())
      result[`action-${index}`] = '请输入进程名';
    if (action.type === 'Delay' && (!Number.isInteger(action.delaySeconds) || Number(action.delaySeconds) <= 0))
      result[`action-${index}`] = '请输入有效的延迟秒数';
    if (action.type === 'Keyboard' && !action.keys.length) result[`action-${index}`] = '请选择至少一个按键';
    if (action.type === 'Shell' && !action.command.trim()) result[`action-${index}`] = '请输入 Shell 命令';
    if (action.type === 'SendNotification' || action.type === 'SendNotificationToChannel') {
      if (!action.notificationContent.trim()) result[`action-${index}`] = '请填写通知内容';
      else if (action.type === 'SendNotificationToChannel' && !action.notificationChannel)
        result[`action-${index}`] = '请选择通知渠道';
    }
  });
  return result;
}
const actionFields: Record<Action['type'], readonly (keyof Action)[]> = {
  StartProgram: ['programPath', 'arguments', 'workingDirectory', 'runElevated', 'silent'],
  StartTag: ['tagName'],
  CloseProcess: ['processName', 'force', 'currentUserOnly'],
  Delay: ['delaySeconds'],
  Keyboard: ['keys'],
  Shell: ['shell', 'command'],
  SendNotification: ['notificationTitle', 'notificationContent', 'notificationChannel'],
  SendNotificationToChannel: ['notificationTitle', 'notificationContent', 'notificationChannel'],
  Extension: ['pluginId', 'actionId', 'parameters'],
};
function actionPayload(action: Action) {
  return {
    type: action.type,
    ...Object.fromEntries(actionFields[action.type].map(key => [key, action[key]])),
    ...(action.continueOnFailure ? { continueOnFailure: true } : {}),
  };
}
async function saveTask() {
  if (!editor.value || safeModeEnabled.value) return;
  const value = JSON.parse(JSON.stringify(editor.value)) as Task;
  value.name = value.name.trim();
  value.trigger.processPaths = value.trigger.processPaths.map((path) => path.trim()).filter(Boolean);
  if (value.actions.length) value.actions[value.actions.length - 1].continueOnFailure = false;
  fieldErrors.value = validate(value);
  if (Object.keys(fieldErrors.value).length) {
    showToast('请检查任务表单中的必填项');
    return;
  }
  const url = originalName.value ? `/api/tasks/${encodeURIComponent(originalName.value)}` : '/api/tasks';
  const currentEditor = editor.value;
  await performOperation('save', url, {
    method: originalName.value ? 'PUT' : 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ...value, actions: value.actions.map(actionPayload) }),
  }, async () => {
    if (editor.value === currentEditor) editor.value = undefined;
    showToast('任务已保存', 'success');
    await loadTasks();
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
  if (!task.enabled) {
    showToast('任务已禁用，无法立即运行');
    return;
  }
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
    const response = await apiFetch('/api/task-variables', { signal: controller.signal });
    if (!response.ok) throw new Error('无法读取变量');
    const variables = await response.json();
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
function copyTextLegacy(text: string, container: HTMLElement): boolean {
  const input = document.createElement('textarea');
  const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  input.value = text;
  input.readOnly = true;
  input.style.position = 'fixed';
  input.style.left = '-9999px';
  input.style.opacity = '0';
  input.setAttribute('aria-hidden', 'true');
  if (!container.isConnected) return false;
  container.append(input);
  try {
    input.select();
    return document.execCommand('copy');
  } catch {
    return false;
  } finally {
    input.remove();
    previousFocus?.focus({ preventScroll: true });
  }
}
async function copyVariable(name: string, event: Event) {
  const container = event.currentTarget instanceof HTMLElement
    ? event.currentTarget.closest<HTMLElement>('[role="dialog"]')
    : null;
  if (!container) return;
  try {
    if (window.isSecureContext && navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(name);
      showToast('变量已复制', 'success');
      return;
    }
  } catch {
  }
  if (copyTextLegacy(name, container)) showToast('变量已复制', 'success');
  else showToast('自动复制失败，请选中文本手动复制');
}
onMounted(() => {
  void loadTasks();
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
  variableHelpController?.abort();
  for (const controller of requestControllers) controller.abort();
  for (const controller of pendingOperations.values()) controller.abort();
  pendingOperations.clear();
  pendingActionOptions.clear();
});
</script>
<template>
  <section class="tasks-view">
    <p v-if="pageError" class="status-message error">{{ pageError }}</p>
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
          <div class="task-row-title">
            <button class="task-name-button" type="button">
              {{ task.name }}
            </button>
            <span v-if="taskWarningText(task)" class="task-warning" role="img" :title="taskWarningText(task)" :aria-label="taskWarningText(task)">
              <AppIcon name="alert-circle" />
            </span>
          </div>
        </div>
        <label
          class="toggle-control"
          @click.stop
        >
          <input type="checkbox" :checked="task.enabled" :disabled="pendingOperations.has(`toggle:${task.name}`)" @change="toggleTaskEnabled(task, $event)" />
        </label>
      </article>
      <p v-if="!tasks.length" class="program-empty">暂无任务，点击“创建任务”开始</p>
    </section>
    <AppModal
      v-if="editor"
      :dismissible="!pendingOperations.has('save')"
      :title="originalName ? '编辑任务' : '创建任务'"
      :dismiss-on-backdrop="false"
      @close="editor = undefined"
    >
      <div class="task-form">
        <label class="field-control">
          <span>任务名称 *</span>
          <input v-model="editor.name" />
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
                { value: 'Cron', label: '按计划执行' },
                { value: 'ProcessStarted', label: '当程序启动时' },
                { value: 'ProcessExited', label: '当程序关闭时' },
                { value: 'WinPilotStartup', label: 'WinPilot 启动时' },
                { value: 'Extension', label: '插件扩展' },
              ]"
              @change="clearTriggerErrors"
            />
          </label>
          <CronEditor
            v-if="editor.trigger.type === 'Cron'"
            v-model="editor.trigger.cron"
            :error="fieldErrors.cron"
          />
          <template
            v-else-if="editor.trigger.type === 'ProcessStarted' || editor.trigger.type === 'ProcessExited'"
          >
            <p>当以下任意一个程序{{ editor.trigger.type === 'ProcessStarted' ? '启动' : '关闭' }}时</p>
            <div class="task-trigger-programs">
              <div
                v-for="(_, index) in editor.trigger.processPaths"
                :key="index"
                class="task-trigger-program-row"
              >
                <input
                  v-model="editor.trigger.processPaths[index]"
                  class="task-trigger-program-path"
                  placeholder="程序绝对路径，例如：C:\Program Files\App\App.exe"
                />
                <button
                  class="icon-button task-trigger-program-remove"
                  type="button"
                  @click="editor.trigger.processPaths.splice(index, 1)"
                >
                  删除
                </button>
              </div>
              <button
                class="button button-secondary task-trigger-program-add"
                type="button"
                @click="editor.trigger.processPaths.push('')"
              >
                ＋ 添加程序
              </button>
            </div>
            <small v-if="fieldErrors.processPaths" class="field-error">{{ fieldErrors.processPaths }}</small>
          </template>
          <div v-else-if="editor.trigger.type === 'Extension'" class="task-trigger-extension">
            <label class="field-control">
              <span>插件 *</span>
              <AppSelect
                :model-value="editor.trigger.pluginId || ''"
                :options="[
                  { value: '', label: '请选择插件' },
                  ...extensionPlugins.map((plugin) => ({
                    value: plugin.pluginId,
                    label: plugin.pluginDisplayName,
                  })),
                  ...(editor.trigger.pluginId &&
                  !extensionPlugins.some((item) => item.pluginId === editor?.trigger.pluginId)
                    ? [{ value: editor.trigger.pluginId, label: `${editor.trigger.pluginId}（不可用）` }]
                    : []),
                ]"
                @change="selectExtensionPlugin($event)"
              />
            </label>
            <label class="field-control">
              <span>触发条件 *</span>
              <AppSelect
                :model-value="editor.trigger.triggerId || ''"
                :options="[
                  { value: '', label: '请选择触发条件' },
                  ...extensionTriggers.map((item) => ({ value: item.triggerId, label: item.triggerName })),
                  ...(editor.trigger.triggerId &&
                  !extensionTriggers.some((item) => item.triggerId === editor?.trigger.triggerId)
                    ? [{ value: editor.trigger.triggerId, label: `${editor.trigger.triggerId}（不可用）` }]
                    : []),
                ]"
                @change="selectExtensionTrigger($event)"
              />
            </label>
            <template v-if="triggerDefinitionFor(editor.trigger.triggerId)?.parameters?.length">
              <label
                v-for="parameter in triggerDefinitionFor(editor.trigger.triggerId)?.parameters"
                :key="parameter.key"
                class="field-control"
              >
                <span>{{ parameter.label }}{{ parameter.required ? ' *' : '' }}</span>
                <AppSelect
                  v-if="parameter.type === 'Select'"
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
                  :model-value="editor.trigger.parameters?.[parameter.key] ?? []"
                  :options="multiParameterOptions(parameter, editor.trigger.parameters?.[parameter.key])"
                  :label="multiSelectionLabel(editor.trigger.parameters?.[parameter.key])"
                  @toggle="(value, checked) => { if (editor) toggleMultiOption(editor.trigger, parameter.key, value, checked); clearTriggerErrors(); }"
                />
                <input
                  v-else-if="parameter.type === 'Checkbox'"
                  type="checkbox"
                  :checked="Boolean(editor.trigger.parameters?.[parameter.key])"
                  @change="selectTriggerParameter(parameter.key, ($event.target as HTMLInputElement).checked)"
                />
                <textarea
                  v-else-if="parameter.type === 'Textarea'"
                  :value="editor.trigger.parameters?.[parameter.key] ?? ''"
                  @input="selectTriggerParameter(parameter.key, ($event.target as HTMLTextAreaElement).value)"
                />
                <input
                  v-else
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
          <article v-for="(action, index) in editor.actions" :key="index" class="task-config-card">
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
            <AppSelect
              :model-value="action.type"
              :options="actionOptions.map(([value, label]) => ({ value, label }))"
              @change="selectActionType(index, $event)"
            />
            <template v-if="action.type === 'Extension'">
              <label class="field-control">
                <span>插件 *</span>
                <AppSelect
                  v-model="action.pluginId"
                  :options="[
                    { value: '', label: '请选择插件' },
                    ...actionPlugins.map((plugin) => ({
                      value: plugin.pluginId,
                      label: plugin.pluginDisplayName,
                    })),
                    ...(action.pluginId && !actionPlugins.some((item) => item.pluginId === action.pluginId)
                      ? [{ value: action.pluginId, label: `${action.pluginId}（不可用）` }]
                      : []),
                  ]"
                  @change="
                    action.actionId = '';
                    action.parameters = {};
                  "
                />
              </label>
              <label class="field-control">
                <span>动作 *</span>
                <AppSelect
                  :model-value="action.actionId || ''"
                  :options="[
                    { value: '', label: '请选择动作' },
                    ...actionDefinitionsFor(action.pluginId).map((definition) => ({
                      value: definition.actionId,
                      label: definition.actionName,
                    })),
                    ...(action.actionId &&
                    !actionDefinitionsFor(action.pluginId).some((item) => item.actionId === action.actionId)
                      ? [{ value: action.actionId, label: `${action.actionId}（不可用）` }]
                      : []),
                  ]"
                  @change="selectExtensionAction(index, $event)"
                />
              </label>
              <template
                v-if="
                  action.actionId &&
                  actionDefinitionsFor(action.pluginId).find((item) => item.actionId === action.actionId)
                "
              >
                <div
                  v-for="parameter in actionDefinitionsFor(action.pluginId).find(
                    (item) => item.actionId === action.actionId,
                  )?.parameters"
                  :key="parameter.key"
                  class="field-control"
                >
                  <span v-if="parameter.type !== 'Checkbox'">
                    {{ parameter.label }}{{ parameter.required ? ' *' : '' }}
                  </span>
                  <textarea
                    v-if="parameter.type === 'Textarea'"
                    v-model="action.parameters![parameter.key]"
                    rows="3"
                  ></textarea>
                  <input
                    v-else-if="parameter.type === 'Number'"
                    v-model.number="action.parameters![parameter.key]"
                    type="number"
                  />
                  <AppSelect
                    v-else-if="parameter.type === 'Select'"
                    v-model="action.parameters![parameter.key]"
                    :before-open="() => loadActionOptions(index, parameter.key)"
                    :disabled="
                      !actionDefinitionsFor(action.pluginId).find((item) => item.actionId === action.actionId)?.dynamicOptions &&
                      !parameter.options?.length &&
                      !unavailableSelectValue(parameter, action.parameters?.[parameter.key])
                    "
                    :options="[
                      { value: '', label: parameter.options?.length ? '请选择配置' : '暂无可用配置' },
                      ...(unavailableSelectValue(parameter, action.parameters?.[parameter.key])
                        ? [
                            {
                              value: unavailableSelectValue(parameter, action.parameters?.[parameter.key]),
                              label: `${unavailableSelectValue(parameter, action.parameters?.[parameter.key])}（不可用）`,
                              disabled: true,
                            },
                          ]
                        : []),
                      ...(parameter.options || []),
                    ]"
                  />
                  <label v-else-if="parameter.type === 'Checkbox'" class="toggle-control">
                    <input v-model="action.parameters![parameter.key]" type="checkbox" />
                    <span>{{ parameter.label }}</span>
                  </label>
                  <AppMultiSelect
                    v-else-if="parameter.type === 'MultiCheckbox'"
                    :before-open="() => loadActionOptions(index, parameter.key)"
                    :model-value="
                      Array.isArray(action.parameters?.[parameter.key])
                        ? action.parameters![parameter.key]
                        : []
                    "
                    :options="multiParameterOptions(parameter, action.parameters?.[parameter.key])"
                    :disabled="
                      !actionDefinitionsFor(action.pluginId).find((item) => item.actionId === action.actionId)?.dynamicOptions &&
                      multiParameterOptions(parameter, action.parameters?.[parameter.key]).length === 0
                    "
                    :label="
                      multiParameterOptions(parameter, action.parameters?.[parameter.key]).length === 0
                        ? '暂无可用配置'
                        : multiSelectionLabel(action.parameters?.[parameter.key])
                    "
                    :aria-label="parameter.label"
                    @toggle="(value, checked) => toggleMultiOption(action, parameter.key, value, checked)"
                  />
                  <input v-else v-model="action.parameters![parameter.key]" type="text" />
                </div>
              </template>
            </template>
            <template v-if="action.type === 'StartProgram'">
              <input v-model="action.programPath" placeholder="程序 / 脚本路径" />
              <input v-model="action.arguments" placeholder="参数" />
              <input v-model="action.workingDirectory" placeholder="起始目录" />
            </template>
            <AppSelect
              v-else-if="action.type === 'StartTag'"
              v-model="action.tagName"
              :options="[
                { value: '', label: '请选择标签' },
                ...tags.map((tag) => ({ value: tag.name, label: tag.name })),
              ]"
            />
            <template v-else-if="action.type === 'CloseProcess'">
              <input v-model="action.processName" placeholder="进程名" />
              <div class="task-process-options">
                <label class="toggle-control">
                  <input v-model="action.force" type="checkbox" />
                  <span>强制关闭</span>
                </label>
                <label class="toggle-control">
                  <input v-model="action.currentUserOnly" type="checkbox" />
                  <span>当前用户</span>
                </label>
              </div>
            </template>
            <input
              v-else-if="action.type === 'Delay'"
              v-model.number="action.delaySeconds"
              type="number"
              min="1"
              step="1"
              title=""
              placeholder="秒"
              aria-label="延迟时间（秒）"
            />
            <template v-else-if="action.type === 'Shell'">
              <AppSelect
                v-model="action.shell"
                :options="[
                  { value: 'PowerShell', label: 'PowerShell' },
                  { value: 'CMD', label: 'CMD' },
                ]"
              />
              <input v-model="action.command" placeholder="输入要执行的命令" />
            </template>
            <template
              v-else-if="action.type === 'SendNotification' || action.type === 'SendNotificationToChannel'"
            >
              <div class="field-control">
                <div class="notification-content-heading">
                  <span>
                    通知内容
                    <b>*</b>
                  </span>
                  <button
                    class="variable-help-button"
                    type="button"
                    title="查看可用变量"
                    aria-label="查看可用变量"
                    @click="openVariableHelp"
                  >
                    <AppIcon name="help" />
                  </button>
                </div>
                <textarea v-model="action.notificationContent" placeholder="通知内容" rows="4"></textarea>
              </div>
              <div v-if="action.type === 'SendNotificationToChannel'" class="field-control">
                <span>
                  发送渠道
                  <b>*</b>
                </span>
                <AppSelect
                  v-model="action.notificationChannel"
                  :options="channelOptions.map(([value, label]) => ({ value, label }))"
                />
              </div>
            </template>
            <KeyboardKeyPicker v-else-if="action.type === 'Keyboard'" v-model="action.keys" />
            <small v-if="fieldErrors[`action-${index}`]" class="field-error">
              {{ fieldErrors[`action-${index}`] }}
            </small>
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
    <AppModal
      v-if="variableHelpOpen"
      title="可用变量"
      :dismiss-on-backdrop="false"
      @close="variableHelpOpen = false"
    >
      <div class="variable-help-content">
        <p class="field-hint">变量来自当前已启用并完成注册的插件</p>
        <p v-if="variableHelpLoading">正在读取变量…</p>
        <p v-else-if="variableHelpError" class="status-message error">{{ variableHelpError }}</p>
        <p v-else-if="!registeredVariables.length">暂无可用变量</p>
        <div v-else class="variable-help-list">
          <article v-for="variable in registeredVariables" :key="variable.name" class="variable-help-item">
            <div class="variable-help-code">
              <code>{{ variable.name }}</code>
              <button
                class="variable-copy-button"
                type="button"
                :title="`复制 ${variable.name}`"
                :aria-label="`复制 ${variable.name}`"
                @click="copyVariable(variable.name, $event)"
              >
                <AppIcon name="copy" />
              </button>
            </div>
            <strong>{{ variable.displayName }}</strong>
            <small>{{ variable.description || '未提供变量说明' }}</small>
            <small>来源：{{ variable.pluginDisplayName }}</small>
          </article>
        </div>
      </div>
    </AppModal>
    <AppModal v-if="detailTarget" :title="detailTarget.name" @close="detailTarget = undefined">
      <template v-if="taskWarningText(detailTarget)" #title-extra>
        <span class="task-warning" role="img" :title="taskWarningText(detailTarget)" :aria-label="taskWarningText(detailTarget)">
          <AppIcon name="alert-circle" />
        </span>
      </template>
      <p>{{ triggerLabel(detailTarget.trigger) }}</p>
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
