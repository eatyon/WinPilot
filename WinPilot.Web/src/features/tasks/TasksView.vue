<script setup lang="ts">
import AppLoadError from '../../components/AppLoadError.vue';
import { useModalState } from '../../ui/useModalState';
import { useDelayedLoading } from '../../ui/useDelayedLoading';
import AppTooltip from '../../components/AppTooltip.vue';
import AppNumberInput from '../../components/AppNumberInput.vue';
import { NFormItem, NEmpty, NAlert, NButton, NInput, NCheckbox, NCard, NCollapseTransition, NSwitch, NInputGroup, NText, NFlex, NSpin } from 'naive-ui';
import {
  useId,
  computed,
  nextTick,
  onBeforeUnmount,
  onMounted,
  reactive,
  ref,
  shallowReactive,
  toRaw,
  watch,
} from 'vue';
import AppIcon from '../../components/AppIcon.vue';
import AppModal from '../../components/AppModal.vue';
import AppSelect from '../../components/AppSelect.vue';
import AppMultiSelect from '../../components/AppMultiSelect.vue';
import TaskActionEditor from './TaskActionEditor.vue';
import TaskVariableHelp from './TaskVariableHelp.vue';
import TaskConfigTransition from './TaskConfigTransition.vue';
import CronEditor from './CronEditor.vue';
import KeyboardKeyPicker from '../../components/KeyboardKeyPicker.vue';
import { orderKeys } from '../../ui/keyboardLayout';
import { normalizeTask, taskPayload, snapshotTask, mapTaskSaveErrors } from './taskSubmission';
import {
  actionOptionsKey,
  describeTask as describeTaskWithContext,
  actionLabel as describeAction,
} from './taskPresentation';
import {
  apiErrorMessage,
  readApiError,
  isAbortError,
  userErrorMessage,
} from '../../api/client';
import { executeRequest } from '../../api/requestExecutor';
import { showToast } from '../../ui/toast';
import { focusInvalidInput } from '../../ui/focusInvalidInput';
import { safeModeEnabled } from '../security/safeMode';
import {
  createTrigger,
  createAction,
  parameterDefaults,
  actionPayload,
  multiSelectionLabel,
  multiParameterOptions,
  toggleMultiOption,
  type Action,
  type Task,
  type ListedTask,
  type RegisteredTrigger,
  type RegisteredAction,
  type RegisteredVariable,
  type ActionOption,
} from './taskModels';
import {
  validateTask,
  unavailableSelectValue,
  invalidParameter,
} from './taskValidation';

const formId = useId();
const editorModal = useModalState<Task | undefined>(() => undefined);
const detailTargetModal = useModalState<ListedTask | undefined>(() => undefined);
const deleteTargetModal = useModalState<Task | undefined>(() => undefined);
const runTargetModal = useModalState<Task | undefined>(() => undefined);
const tasks = ref<ListedTask[]>([]),
  tasksLoaded = ref(false),
  tasksReadState = ref<'loading' | 'ready' | 'error'>('loading'),
  tags = ref<{ name: string }[]>([]),
  registeredTriggers = ref<RegisteredTrigger[]>([]),
  registeredActions = ref<RegisteredAction[]>([]),
  registeredVariables = ref<RegisteredVariable[]>([]),
  editor = editorModal.data,
  originalName = ref(''),
  detailTarget = detailTargetModal.data,
  deleteTarget = deleteTargetModal.data,
  runTarget = runTargetModal.data,
  pageError = ref(''),
  fieldErrors = ref<Record<string, string>>({}),
  taskSearch = ref('');
const variableHelpOpenModal = useModalState(() => false);
const variableHelpOpen = variableHelpOpenModal.data,
  variableHelpLoading = ref(false);
let isPageActive = true;
let loadController: AbortController | undefined;
let extensionsController: AbortController | undefined;
const extensionsLoaded = ref(false),
  extensionsLoading = ref(false),
  extensionsError = ref('');
let variableHelpController: AbortController | undefined;
const variableHelpAction = ref<Action>();
const requestControllers = new Set<AbortController>();
const pendingOperations = shallowReactive(new Map<string, AbortController>());

async function runPageRequest<T>(
  url: string,
  init: RequestInit,
  handleResponse: (response: Response) => T | Promise<T>,
): Promise<T> {
  const controller = new AbortController();
  if (!isPageActive) controller.abort();
  requestControllers.add(controller);
  try {
    return await executeRequest(url, init, controller, handleResponse);
  } finally {
    requestControllers.delete(controller);
  }
}

async function performOperation(
  key: string,
  url: string,
  init: RequestInit,
  success: () => Promise<void> | void,
  readFailure?: (response: Response, signal: AbortSignal) => Promise<string>,
) {
  if (!isPageActive || pendingOperations.has(key)) return;
  const controller = new AbortController();
  pendingOperations.set(key, controller);
  try {
    await runPageRequest(url, { ...init, signal: controller.signal }, async (response) => {
      if (!response.ok)
        throw new Error(
          readFailure
            ? await readFailure(response, controller.signal)
            : await readApiError(response, '操作失败'),
        );
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
const processPathIdentities = new WeakMap<string[], symbol[]>();
function processPathKeys(paths: string[]) {
  const identity = toRaw(paths);
  let keys = processPathIdentities.get(identity);
  if (!keys) {
    keys = identity.map(() => Symbol());
    processPathIdentities.set(identity, keys);
  }
  return keys;
}
function addProcessPath(paths: string[]) {
  processPathKeys(paths).push(Symbol());
  paths.push('');
}
function removeProcessPath(paths: string[], index: number) {
  if (!Number.isInteger(index) || index < 0 || index >= paths.length) return;
  processPathKeys(paths).splice(index, 1);
  paths.splice(index, 1);
}
function actionKey(action: Action) {
  const identity = toRaw(action);
  let key = actionKeys.get(identity);
  if (!key) {
    key = Symbol();
    actionKeys.set(identity, key);
  }
  return key;
}
const channelOptions = ref<{ value: string; label: string }[]>([]);
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
      const result = await runPageRequest(
        type === 'StartTag' ? '/api/programs/tags' : '/api/notifications/channels',
        {},
        async (response) => {
          if (!response.ok) throw new Error(await readApiError(response, message));
          return response.json();
        },
      );
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
watch(
  () => editor.value?.actions.map((action) => action.type),
  (types) => {
    for (const type of new Set(types))
      if (type === 'StartTag' || type === 'SendNotification') void loadActionResource(type);
  },
  { flush: 'sync' },
);
const actionErrors = shallowReactive(
  new Map<Action, { message: string; snapshot: string; field?: string }>(),
);
const actionValidationFields: Partial<Record<Action['type'], string>> = {
  StartProgram: 'programPath',
  StartTag: 'tagName',
  EndProcess: 'processName',
  Delay: 'delaySeconds',
  Keyboard: 'keys',
  Shell: 'command',
};
const editorForm = ref<HTMLElement>();
const triggerErrorField = ref('');
const invalidProcessPath = computed(() =>
  Math.max(
    0,
    editor.value?.trigger.processPaths.findIndex(
      (path) => path.trim() && !/^(?:[a-zA-Z]:[\\/]|\\\\)/.test(path.trim()),
    ) ?? -1,
  ),
);
const invalidTriggerField = computed(() => {
  if (!fieldErrors.value.extension || !editor.value) return undefined;
  const trigger = editor.value.trigger;
  if (triggerErrorField.value) return triggerErrorField.value;
  if (!trigger.pluginId) return 'pluginId';
  if (!trigger.triggerId) return 'triggerId';
  const parameter = invalidParameter(
    triggerDefinitionFor(trigger.triggerId)?.parameters,
    trigger.parameters,
  );
  return parameter ? `parameters.${parameter.key}` : 'triggerId';
});
const inputSelector =
  'input:not(:disabled), textarea:not(:disabled), .n-base-selection:not(.n-base-selection--disabled), [role="checkbox"]:not([aria-disabled="true"]), .keyboard-expand-toggle:not(:disabled), .task-trigger-program-add:not(:disabled)';
const unassignedTriggerError = computed(() => {
  const field = invalidTriggerField.value;
  const parameters = triggerDefinitionFor(editor.value?.trigger.triggerId ?? '')?.parameters ?? [];
  const fields = ['pluginId', 'triggerId', ...parameters.map(parameter => `parameters.${parameter.key}`)];
  return fieldErrors.value.extension && !fields.includes(field ?? '') ? fieldErrors.value.extension : undefined;
});
function fieldInput(container: HTMLElement | null | undefined, field: string) {
  const target = Array.from(
    container?.querySelectorAll<HTMLElement>('[data-task-field]') ?? [],
  ).find((element) => element.dataset.taskField === field);
  return target?.matches(inputSelector)
    ? target
    : target?.querySelector<HTMLElement>(inputSelector);
}
function invalidActionField(action: Action) {
  const error = actionErrors.get(action);
  if (!error) return undefined;
  const field = error.field ?? actionValidationFields[action.type];
  if (field !== undefined) return field;
  if (action.type === 'SendNotification')
    return action.notificationContent.trim() ? 'notificationChannel' : 'notificationContent';
  if (action.type === 'TaskState') return action.taskName.trim() ? 'state' : 'taskName';
  if (!action.pluginId) return 'pluginId';
  if (!action.actionId) return 'actionId';
  const parameter = invalidParameter(actionDefinitionFor(action)?.parameters, action.parameters);
  return parameter ? `parameters.${parameter.key}` : 'actionId';
}
async function focusTaskError(currentEditor: Task, signal?: AbortSignal) {
  await nextTick();
  if (!isPageActive || signal?.aborted || editor.value !== currentEditor) return;
  for (const field of ['name', 'cron', 'event', 'processPaths', 'keys', 'extension']) {
    if (!fieldErrors.value[field]) continue;
    const target = field === 'extension' ? (invalidTriggerField.value ?? field) : field;
    let input = fieldInput(editorForm.value, target);
    if (field === 'processPaths') {
      input =
        fieldInput(editorForm.value, `processPaths.${invalidProcessPath.value}`) ??
        fieldInput(editorForm.value, 'addProcess');
    }
    if (input) {
      focusInvalidInput(
        input,
        field === 'keys' ? input.closest<HTMLElement>('.selected-keys') : undefined,
      );
      return;
    }
  }
  for (const [index, action] of currentEditor.actions.entries()) {
    const field = invalidActionField(action);
    if (!field) continue;
    const container = editorForm.value?.querySelector<HTMLElement>(`[data-task-action="${index}"]`);
    const input = fieldInput(container, field) ?? fieldInput(container, 'type');
    if (input) {
      focusInvalidInput(
        input,
        field === 'keys' ? input.closest<HTMLElement>('.selected-keys') : undefined,
      );
      return;
    }
  }
}
function clearTriggerErrors() {
  triggerErrorField.value = '';
  delete fieldErrors.value.cron;
  delete fieldErrors.value.event;
  delete fieldErrors.value.processPaths;
  delete fieldErrors.value.keys;
  delete fieldErrors.value.extension;
}
watch(
  () => editor.value?.name,
  () => {
    delete fieldErrors.value.name;
  },
  { flush: 'sync' },
);
watch(() => JSON.stringify(editor.value?.trigger), clearTriggerErrors, { flush: 'sync' });
watch(
  () =>
    editor.value?.actions.map((action) => [action, JSON.stringify(actionPayload(action))] as const),
  (current) => {
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
  return registeredActions.value.find(
    (item) => item.pluginId === action?.pluginId && item.actionId === action?.actionId,
  );
}
const pendingActionOptions = new Map<string, Promise<ActionOption[]>>();
const detailActionOptions = ref(new Map<string, ActionOption[]>());
function fetchActionOptions(
  pluginId: string,
  actionId: string,
  parameterKey: string,
): Promise<ActionOption[]> {
  const key = actionOptionsKey(pluginId, actionId, parameterKey);
  const pending = pendingActionOptions.get(key);
  if (pending) return pending;
  const request = (async () => {
    const options = await runPageRequest<unknown>(
      `/api/tasks/extensions/actions/${encodeURIComponent(pluginId)}/${encodeURIComponent(actionId)}/options/${encodeURIComponent(parameterKey)}`,
      {},
      (response) => {
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
watch(
  [detailTarget, registeredActions],
  ([task], _, onCleanup) => {
    detailActionOptions.value = new Map();
    if (!task) return;
    let active = true;
    onCleanup(() => {
      active = false;
    });
    const requests = task.actions.flatMap((action) => {
      const definition = action.type === 'Extension' ? actionDefinitionFor(action) : undefined;
      if (!definition?.available || !definition.dynamicOptions) return [];
      return definition.parameters
        .filter((parameter) => parameter.type === 'Select' || parameter.type === 'MultiCheckbox')
        .map(async (parameter) => {
          const { pluginId, actionId } = definition;
          const options = await fetchActionOptions(pluginId, actionId, parameter.key);
          if (active && isPageActive)
            detailActionOptions.value.set(
              actionOptionsKey(pluginId, actionId, parameter.key),
              options,
            );
        });
    });
    void Promise.all(requests).catch(() => {
      if (active && isPageActive) showToast('读取动作配置失败');
    });
  },
  { flush: 'sync' },
);
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
    // 加载期间动作可能被移动、删除或切换扩展，结果只应用到原编辑器中的原动作。
    const current = currentEditor?.actions[index];
    if (current !== action || current?.pluginId !== pluginId || current.actionId !== actionId)
      return false;
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
const taskRows = computed(() => tasks.value.map((task) => ({ task, ...describeTask(task) })));
const filteredTasks = computed(() => {
  const keyword = taskSearch.value.trim().toLocaleLowerCase();
  return keyword
    ? taskRows.value.filter(({ task }) => task.name.toLocaleLowerCase().includes(keyword))
    : taskRows.value;
});
const taskEmptyDescription = computed(() => {
  if (tasks.value.length) return filteredTasks.value.length ? '' : '没有匹配的任务';
  if (pageError.value) return '无法加载任务列表';
  return tasksLoaded.value ? '暂无任务，点击“创建任务”开始' : '';
});
const detailDisplay = computed(() => {
  const task = detailTarget.value;
  return task ? (taskRows.value.find((row) => row.task === task) ?? describeTask(task)) : undefined;
});
const presentationContext = computed(() => ({
  triggers: registeredTriggers.value,
  actions: registeredActions.value,
  extensionsLoaded: extensionsLoaded.value,
  actionOptions: detailActionOptions.value,
}));
const describeTask = (task: ListedTask) => describeTaskWithContext(task, presentationContext.value);
const actionLabel = (action: Action, index: number, total: number) =>
  describeAction(action, index, total, presentationContext.value);
function openTaskEditor(task?: Task) {
  const savedTask = task ? (JSON.parse(JSON.stringify(task)) as Task) : undefined;
  editorModal.open({
    name: savedTask?.name ?? '',
    enabled: savedTask?.enabled ?? true,
    trigger: { ...createTrigger(), ...savedTask?.trigger },
    actions: savedTask
      ? savedTask.actions.map((action) => ({
          ...createAction(),
          ...action,
          ...(action.type === 'Keyboard' ? { keys: orderKeys(action.keys ?? []) } : {}),
        }))
      : [createAction()],
  });
  if (editor.value?.trigger.type === 'Keyboard')
    editor.value.trigger.keys = orderKeys(editor.value.trigger.keys);
  originalName.value = task?.name ?? '';
  detailTargetModal.close();
  fieldErrors.value = {};
  actionErrors.clear();
  pageError.value = '';
  editor.value?.actions.forEach((action, index) => {
    if (action.type === 'Extension') loadActionOptionsForAction(index);
  });
}
async function loadTasks() {
  tasksReadState.value = 'loading';
  loadController?.abort();
  const controller = new AbortController();
  loadController = controller;
  try {
    const result = await runPageRequest(
      '/api/tasks',
      { signal: controller.signal },
      async (response) => {
        if (!response.ok) throw new Error(await readApiError(response, '操作失败'));
        return response.json();
      },
    );
    if (!isPageActive || loadController !== controller || controller.signal.aborted) return;
    tasks.value = result;
    tasksLoaded.value = true;
    tasksReadState.value = 'ready';
    pageError.value = '';
  } catch (cause) {
    if (
      isPageActive &&
      loadController === controller &&
      !controller.signal.aborted &&
      !isAbortError(cause)
    ) {
      pageError.value = userErrorMessage(cause, '加载任务失败');
      tasksReadState.value = 'error';
    }
  } finally {
    if (loadController === controller) loadController = undefined;
  }
}
async function loadExtensions() {
  extensionsController?.abort();
  const controller = new AbortController();
  extensionsController = controller;
  extensionsLoading.value = true;
  try {
    const [triggers, actions] = await Promise.all(
      ['triggers', 'actions'].map((kind) =>
        runPageRequest(
          `/api/tasks/extensions/${kind}`,
          { signal: controller.signal },
          async (response) => {
            if (!response.ok)
              throw new Error(await readApiError(response, '无法读取插件扩展，请重试'));
            return response.json();
          },
        ),
      ),
    );
    if (!isPageActive || extensionsController !== controller || controller.signal.aborted) return;
    registeredTriggers.value = triggers;
    registeredActions.value = actions;
    extensionsLoaded.value = true;
    extensionsError.value = '';
    editor.value?.actions.forEach((action, index) => {
      if (action.type === 'Extension') loadActionOptionsForAction(index);
    });
  } catch (cause) {
    if (
      isPageActive &&
      extensionsController === controller &&
      !controller.signal.aborted &&
      !isAbortError(cause)
    )
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
const validate = (value: Task) =>
  validateTask(value, registeredTriggers.value, registeredActions.value);
async function saveTask() {
  if (!editor.value || safeModeEnabled.value || pendingOperations.has('save')) return;
  const value = normalizeTask(editor.value);
  const errors = validate(value);
  if (Object.keys(errors).length) {
    triggerErrorField.value = '';
    fieldErrors.value = Object.fromEntries(
      Object.entries(errors).filter(([key]) => !key.startsWith('action-')),
    );
    actionErrors.clear();
    editor.value.actions.forEach((action, index) => {
      const message = errors[`action-${index}`];
      if (message)
        actionErrors.set(action, { message, snapshot: JSON.stringify(actionPayload(action)) });
    });
    showToast('请检查任务表单中的配置');
    await focusTaskError(editor.value);
    return;
  }
  const url = originalName.value
    ? `/api/tasks/${encodeURIComponent(originalName.value)}`
    : '/api/tasks';
  const currentEditor = editor.value;
  const submitted = snapshotTask(currentEditor);
  let validationSignal: AbortSignal | undefined;
  await performOperation(
    'save',
    url,
    {
      method: originalName.value ? 'PUT' : 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(taskPayload(value)),
    },
    async () => {
      if (editor.value === currentEditor) editorModal.close();
      showToast('任务已保存', 'success');
      await loadTasks();
    },
    async (response, signal) => {
      let problem: unknown;
      try {
        problem = await response.json();
      } catch (cause) {
        if (isAbortError(cause)) throw cause;
        return '操作失败';
      }
      let applied = false;
      if (
        (response.status === 400 || response.status === 409) &&
        isPageActive &&
        !signal.aborted &&
        editor.value === currentEditor
      ) {
        const mapped = mapTaskSaveErrors(problem, currentEditor, submitted);
        if (mapped) {
          triggerErrorField.value = mapped.triggerErrorField;
          fieldErrors.value = mapped.fields;
          actionErrors.clear();
          for (const [action, error] of mapped.actions) actionErrors.set(action, error);
          applied = Object.keys(mapped.fields).length > 0 || mapped.actions.size > 0;
        }
      }
      if (applied) validationSignal = signal;
      return applied ? '请检查任务表单中的配置' : apiErrorMessage(problem, '操作失败');
    },
  );
  if (validationSignal) await focusTaskError(currentEditor, validationSignal);
}
async function deleteTask() {
  const target = deleteTarget.value;
  if (!target || safeModeEnabled.value) return;
  await performOperation(
    'delete',
    `/api/tasks/${encodeURIComponent(target.name)}`,
    { method: 'DELETE' },
    async () => {
      if (deleteTarget.value === target) deleteTargetModal.close();
      await loadTasks();
    },
  );
}
async function toggleTaskEnabled(task: Task, event?: Event) {
  if (event && event.target instanceof HTMLInputElement) event.target.checked = task.enabled;
  await performOperation(
    `toggle:${task.name}`,
    `/api/tasks/${encodeURIComponent(task.name)}/enabled`,
    {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled: !task.enabled }),
    },
    loadTasks,
  );
}

function requestRun(task: Task) {
  runTargetModal.open(task);
  detailTargetModal.close();
}
async function runTask() {
  const target = runTarget.value;
  if (!target) return;
  await performOperation(
    'run',
    `/api/tasks/${encodeURIComponent(target.name)}/run`,
    { method: 'POST' },
    () => {
      if (runTarget.value === target) runTargetModal.close();
    },
  );
}
function move(items: unknown[], index: number, delta: number) {
  const next = index + delta;
  if (next < 0 || next >= items.length) return;
  const [item] = items.splice(index, 1);
  items.splice(next, 0, item);
}
async function openVariableHelp(action?: Action) {
  if (!isPageActive || variableHelpLoading.value || variableHelpOpenModal.show) return;
  variableHelpController?.abort();
  const controller = new AbortController();
  variableHelpController = controller;
  variableHelpAction.value = action;
  variableHelpLoading.value = true;
  try {
    const variables = await runPageRequest(
      '/api/tasks/variables',
      { signal: controller.signal },
      (response) => {
        if (!response.ok) throw new Error('无法读取变量');
        return response.json();
      },
    );
    if (
      isPageActive &&
      variableHelpController === controller &&
      !controller.signal.aborted
    ) {
      registeredVariables.value = variables;
      variableHelpOpenModal.open(true);
    }
  } catch (cause) {
    if (
      !isPageActive ||
      controller.signal.aborted ||
      variableHelpController !== controller ||
      isAbortError(cause)
    )
      return;
    showToast(userErrorMessage(cause, '无法读取变量'), 'error');
  } finally {
    if (variableHelpController === controller) {
      variableHelpLoading.value = false;
      variableHelpController = undefined;
      variableHelpAction.value = undefined;
    }
  }
}
const showVariableHelpLoading = useDelayedLoading(() => variableHelpLoading.value);
onMounted(() => {
  void loadTasks();
  void loadExtensions();
});
watch(
  () => editorModal.show,
  (open) => {
    if (!open) variableHelpController?.abort();
  },
  { flush: 'sync' },
);
watch(
  () => variableHelpOpenModal.show,
  (open) => {
    if (!open) variableHelpController?.abort();
  },
  { flush: 'sync' },
);
watch(safeModeEnabled, (enabled) => {
  if (enabled) {
    editorModal.reset();
    deleteTargetModal.reset();
    for (const [key, controller] of pendingOperations)
      if (key === 'save' || key === 'delete') controller.abort();
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
function retryPageLoads() {
  return Promise.all([loadTasks(), loadExtensions()]);
}
</script>
<template>
  <section class="tasks-view load-error-layout">
    <AppLoadError
      :show="Boolean(pageError || extensionsError)"
      :retry="retryPageLoads"
      :disabled="extensionsLoading"
    >{{ pageError || extensionsError }}</AppLoadError>
    <NCard tag="section" class="task-list">
      <div class="content-section-header">
        <div class="content-actions">
          <label class="tag-search">
            <NInput
              v-model:value="taskSearch"
              clearable
              placeholder="搜索任务名"
              :disabled="!tasks.length"
              :input-props="{ role: 'searchbox', enterkeyhint: 'search', 'aria-label': '搜索任务名' }"
            />
          </label>
          <NButton
            attr-type="button"
            :disabled="safeModeEnabled"
            @click="openTaskEditor()"
            type="primary"
          >
            <template #icon><AppIcon name="plus" /></template>
            创建任务
          </NButton>
        </div>
      </div>
      <NFlex
        v-if="tasksReadState === 'loading' && !tasksLoaded && !pageError"
        class="content-empty"
        align="center"
        justify="center"
        :size="8"
        role="status"
        aria-busy="true"
      >
        <NSpin :size="16" />
        <NText :depth="3">正在读取任务</NText>
      </NFlex>
      <article
        v-for="{ task, warning } in filteredTasks"
        :key="task.name"
        class="task-row"
        @click="detailTargetModal.open(task)"
      >
        <div class="task-row-main">
          <NButton class="task-name-button" text strong size="large" attr-type="button">
            <span class="task-name-copy">{{ task.name }}</span>
          </NButton>
        </div>
        <div class="task-row-controls">
          <AppTooltip v-if="warning" :text="warning">
            <NText
              type="warning"
              class="task-warning"
              role="img"
              :aria-label="warning"
            >
              <AppIcon name="alert-circle" />
            </NText>
          </AppTooltip>
          <NSwitch
            :value="task.enabled"
            :disabled="pendingOperations.has(`toggle:${task.name}`)"
            :aria-label="`${task.enabled ? '禁用' : '启用'}任务 ${task.name}`"
            @click.stop
            @update:value="toggleTaskEnabled(task)"
          />
        </div>
      </article>
      <NEmpty v-if="taskEmptyDescription" :show-icon="false" :description="taskEmptyDescription" class="content-empty" />
    </NCard>
    <AppModal
      :show="editorModal.show"
      @after-leave="editorModal.afterLeave"
      v-if="editor"
      :dismissible="!pendingOperations.has('save')"
      :title="originalName ? '编辑任务' : '创建任务'"
      :dismiss-on-backdrop="false"
      @close="editorModal.close()"
    >
      <div ref="editorForm" class="task-form" :inert="pendingOperations.has('save')">
        <NFormItem
          label="任务名称"
          required
          :label-props="{ for: `${formId}-field-0` }"
          :show-feedback="Boolean(fieldErrors.name)"
          :validation-status="fieldErrors.name ? 'error' : undefined"
          class="form-field"
          data-task-field="name"
        >
          <NInput
            v-model:value="editor.name"
            :status="Boolean(fieldErrors.name) || undefined ? 'error' : undefined"
            :input-props="{ id: `${formId}-field-0`, 'aria-invalid': Boolean(fieldErrors.name) || undefined }"
          />
          <template #feedback><span role="alert">{{ fieldErrors.name }}</span></template>
        </NFormItem>
        <NCheckbox v-model:checked="editor.enabled">启用任务</NCheckbox>
        <div class="task-form-section">
          <div class="task-section-heading">
            <div>
              <h3>触发条件</h3>
              <p>选择任务执行的时机</p>
            </div>
          </div>
          <NFormItem
            label="类型"
            :label-props="{ id: (`${formId}-field-1`) + '-label' }"
            :show-feedback="false"
            class="form-field"
          >
            <AppSelect
              :labelled-by="(`${formId}-field-1`) + '-label'"
              :id="`${formId}-field-1`"
              v-model="editor.trigger.type"
              :options="[
                { value: 'Cron', label: 'Cron 计划' },
                { value: 'Process', label: '程序事件' },
                { value: 'WinPilotStartup', label: 'WinPilot 启动' },
                { value: 'Keyboard', label: '按键监听' },
                { value: 'Extension', label: '插件扩展' },
              ]"
            />
          </NFormItem>
          <TaskConfigTransition :value="editor.trigger.type">
            <CronEditor
              data-task-field="cron"
              v-if="editor.trigger.type === 'Cron'"
              v-model="editor.trigger.cron"
              v-model:error="fieldErrors.cron"
            />
            <NFormItem
              v-else-if="editor.trigger.type === 'Keyboard'"
              :show-label="false"
              :show-feedback="Boolean(fieldErrors.keys)"
              :validation-status="fieldErrors.keys ? 'error' : undefined"
              :feedback="fieldErrors.keys"
              class="form-field task-trigger-keyboard"
              content-class="form-field-content"
              data-task-field="keys"
            >
              <KeyboardKeyPicker
                v-model="editor.trigger.keys"
                :invalid="Boolean(fieldErrors.keys)"
                collapsible
              />
            </NFormItem>
            <div v-else-if="editor.trigger.type === 'Process'" class="task-trigger-process">
              <NFormItem
                label="事件"
                required
                :label-props="{ id: (`${formId}-field-2`) + '-label' }"
                :show-feedback="Boolean(fieldErrors.event)"
                :validation-status="fieldErrors.event ? 'error' : undefined"
                class="form-field"
                data-task-field="event"
              >
                <AppSelect
                  :labelled-by="(`${formId}-field-2`) + '-label'"
                  :id="`${formId}-field-2`"
                  v-model="editor.trigger.event"
                  :invalid="Boolean(fieldErrors.event)"
                  :options="[
                    { value: 'Started', label: '程序启动时' },
                    { value: 'Exited', label: '程序退出时' },
                  ]"
                />
                <template #feedback><span role="alert">{{ fieldErrors.event }}</span></template>
              </NFormItem>
              <NCollapseTransition :show="Boolean(editor.trigger.event)" :inert="!editor.trigger.event || undefined">
                <NFormItem
                  :show-label="false"
                  :show-feedback="Boolean(fieldErrors.processPaths)"
                  :validation-status="fieldErrors.processPaths ? 'error' : undefined"
                  :feedback="fieldErrors.processPaths"
                  class="form-field task-trigger-program-reveal"
                  content-class="form-field-content task-trigger-programs"
                  data-task-field="processPaths"
                >
                  <TransitionGroup name="task-action" @before-leave="(element) => element.setAttribute('inert', '')">
                    <div
                      v-for="(_, index) in editor.trigger.processPaths"
                      :key="processPathKeys(editor.trigger.processPaths)[index]"
                      :data-task-field="`processPaths.${index}`"
                      class="task-action-transition"
                    >
                      <div class="task-action-content">
                        <div class="task-trigger-path-spacing">
                          <NInputGroup>
                            <NInput
                              v-model:value="editor.trigger.processPaths[index]"
                              :status="
                                Boolean(fieldErrors.processPaths && index === invalidProcessPath) || undefined
                                  ? 'error'
                                  : undefined
                              "
                              aria-label="程序绝对路径"
                              placeholder="程序绝对路径，例如：C:\Program Files\App\App.exe"
                              :input-props="{
                                'aria-invalid':
                                  Boolean(fieldErrors.processPaths && index === invalidProcessPath) ||
                                  undefined,
                              }"
                            />
                            <NButton
                              class="app-icon-button"
                              aria-label="删除"
                              :theme-overrides="{
                                textColorHover: 'var(--danger-solid-hover)',
                                textColorPressed: 'var(--danger-solid-hover)',
                                textColorFocus: 'var(--danger-solid-hover)',
                                borderHover: '1px solid var(--danger-solid-hover)',
                                borderPressed: '1px solid var(--danger-solid-hover)',
                                borderFocus: '1px solid var(--danger-solid-hover)',
                                rippleColor: 'var(--danger-solid-hover)',
                              }"
                              @click="removeProcessPath(editor.trigger.processPaths, index)"
                            >
                              <template #icon><AppIcon name="close" /></template>
                            </NButton>
                          </NInputGroup>
                        </div>
                      </div>
                    </div>
                  </TransitionGroup>
                  <div class="task-trigger-program-controls">
                    <NButton
                      class="task-trigger-program-add"
                      data-task-field="addProcess"
                      :aria-invalid="
                        Boolean(fieldErrors.processPaths && !editor.trigger.processPaths.length) ||
                        undefined
                      "
                      attr-type="button"
                      @click="addProcessPath(editor.trigger.processPaths)"
                    >
                      <template #icon><AppIcon name="plus" /></template>
                  添加程序
                    </NButton>
                    <p class="field-hint">任一程序发生所选事件时触发任务</p>
                  </div>
                </NFormItem>
              </NCollapseTransition>
            </div>
            <div v-else-if="editor.trigger.type === 'Extension'" class="task-trigger-extension">
              <div class="task-trigger-fields">
                <NFormItem
                  label="插件"
                  required
                  :label-props="{ id: (`${formId}-field-3`) + '-label' }"
                  :show-feedback="invalidTriggerField === 'pluginId'"
                  :validation-status="invalidTriggerField === 'pluginId' ? 'error' : undefined"
                  :feedback="invalidTriggerField === 'pluginId' ? fieldErrors.extension : undefined"
                  class="form-field"
                  data-task-field="pluginId"
                >
                  <AppSelect
                    :labelled-by="(`${formId}-field-3`) + '-label'"
                    :id="`${formId}-field-3`"
                    :model-value="editor.trigger.pluginId || ''"
                    :disabled="!extensionsLoaded"
                    :invalid="invalidTriggerField === 'pluginId'"
                    :options="[
                      ...extensionPlugins.map((plugin) => ({
                        value: plugin.pluginId,
                        label: plugin.pluginDisplayName,
                      })),
                      ...(editor.trigger.pluginId &&
                      !extensionPlugins.some((item) => item.pluginId === editor?.trigger.pluginId)
                        ? [
                            {
                              value: editor.trigger.pluginId,
                              label: `${editor.trigger.pluginId}${extensionsLoaded ? '（不可用）' : ''}`,
                            },
                          ]
                        : []),
                    ]"
                    @change="selectExtensionPlugin($event)"
                  />
                </NFormItem>
                <NFormItem
                  label="触发条件"
                  required
                  :label-props="{ id: (`${formId}-field-4`) + '-label' }"
                  :show-feedback="invalidTriggerField === 'triggerId'"
                  :validation-status="invalidTriggerField === 'triggerId' ? 'error' : undefined"
                  :feedback="invalidTriggerField === 'triggerId' ? fieldErrors.extension : undefined"
                  class="form-field"
                  data-task-field="triggerId"
                >
                  <AppSelect
                    :labelled-by="(`${formId}-field-4`) + '-label'"
                    :id="`${formId}-field-4`"
                    :model-value="editor.trigger.triggerId || ''"
                    :disabled="!extensionsLoaded"
                    :invalid="invalidTriggerField === 'triggerId'"
                    :options="[
                      ...extensionTriggers.map((item) => ({
                        value: item.triggerId,
                        label: item.triggerName,
                      })),
                      ...(editor.trigger.triggerId &&
                      !extensionTriggers.some((item) => item.triggerId === editor?.trigger.triggerId)
                        ? [
                            {
                              value: editor.trigger.triggerId,
                              label: `${editor.trigger.triggerId}${extensionsLoaded ? '（不可用）' : ''}`,
                            },
                          ]
                        : []),
                    ]"
                    @change="selectExtensionTrigger($event)"
                  />
                </NFormItem>
                <template v-if="triggerDefinitionFor(editor.trigger.triggerId)?.parameters?.length">
                  <NFormItem
                    :required="parameter.required"
                    :label-props="{ id: (`${formId}-field-5-${parameter.key}`) + '-label' }"
                    :show-feedback="invalidTriggerField === `parameters.${parameter.key}`"
                    :validation-status="invalidTriggerField === `parameters.${parameter.key}` ? 'error' : undefined"
                    :feedback="invalidTriggerField === `parameters.${parameter.key}` ? fieldErrors.extension : undefined"
                    v-for="parameter in triggerDefinitionFor(editor.trigger.triggerId)?.parameters"
                    :key="parameter.key"
                    :data-task-field="`parameters.${parameter.key}`"
                    class="form-field"
                  >
                    <template #label>{{ parameter.label }}</template>
                    <AppSelect
                      :labelled-by="(`${formId}-field-5-${parameter.key}`) + '-label'"
                      :id="`${formId}-field-5-${parameter.key}`"
                      v-if="parameter.type === 'Select'"
                      :invalid="invalidTriggerField === `parameters.${parameter.key}`"
                      :model-value="(editor.trigger.parameters?.[parameter.key] ?? '') as string"
                      :options="[
                        ...(parameter.options || []),
                        ...(unavailableSelectValue(
                          parameter,
                          editor.trigger.parameters?.[parameter.key],
                        )
                          ? [
                              {
                                value: unavailableSelectValue(
                                  parameter,
                                  editor.trigger.parameters?.[parameter.key],
                                ),
                                label: `${unavailableSelectValue(parameter, editor.trigger.parameters?.[parameter.key])}（不可用）`,
                              },
                            ]
                          : []),
                      ]"
                      @change="selectTriggerParameter(parameter.key, $event)"
                    />
                    <AppMultiSelect
                      :labelled-by="(`${formId}-field-5-${parameter.key}`) + '-label'"
                      :id="`${formId}-field-5-${parameter.key}`"
                      v-else-if="parameter.type === 'MultiCheckbox'"
                      :invalid="invalidTriggerField === `parameters.${parameter.key}`"
                      :model-value="(editor.trigger.parameters?.[parameter.key] ?? []) as string[]"
                      :options="
                        multiParameterOptions(parameter, editor.trigger.parameters?.[parameter.key])
                      "
                      :label="multiSelectionLabel(editor.trigger.parameters?.[parameter.key])"
                      @toggle="
                        (value, checked) => {
                          if (editor) toggleMultiOption(editor.trigger, parameter.key, value, checked);
                        }
                      "
                    />
                    <NCheckbox
                      v-else-if="parameter.type === 'Checkbox'"
                      :aria-label="parameter.label"
                      :input-props="{
                        'aria-invalid':
                          invalidTriggerField === `parameters.${parameter.key}` || undefined,
                      }"
                      :checked="Boolean(editor.trigger.parameters?.[parameter.key])"
                      @update:checked="selectTriggerParameter(parameter.key, $event)"
                    />
                    <NInput
                      v-else-if="parameter.type === 'Textarea'"
                      :status="
                        invalidTriggerField === `parameters.${parameter.key}` || undefined
                          ? 'error'
                          : undefined
                      "
                      :value="(editor.trigger.parameters?.[parameter.key] ?? '') as string"
                      @update:value="selectTriggerParameter(parameter.key, $event)"
                      :input-props="{ id: `${formId}-field-5-${parameter.key}`,
                        'aria-invalid':
                          invalidTriggerField === `parameters.${parameter.key}` || undefined,
                      }"
                      type="textarea"
                    />
                    <AppNumberInput
                      v-else-if="parameter.type === 'Number'"
                      :id="`${formId}-field-5-${parameter.key}`"
                      :model-value="editor.trigger.parameters?.[parameter.key] as number | string | null | undefined"
                      :aria-invalid="invalidTriggerField === `parameters.${parameter.key}` || undefined"
                      @update:model-value="selectTriggerParameter(parameter.key, $event === '' ? Number.NaN : $event)"
                    />
                    <NInput
                      v-else
                      :status="
                        invalidTriggerField === `parameters.${parameter.key}` || undefined
                          ? 'error'
                          : undefined
                      "
                      :value="(editor.trigger.parameters?.[parameter.key] ?? '') as string"
                      @update:value="selectTriggerParameter(parameter.key, $event)"
                      :input-props="{ id: `${formId}-field-5-${parameter.key}`,
                        'aria-invalid':
                          invalidTriggerField === `parameters.${parameter.key}` || undefined,
                      }"
                    />
                  </NFormItem>
                </template>
              </div>
              <NCollapseTransition :show="Boolean(unassignedTriggerError)">
                <div class="task-validation-feedback">
                  <NAlert type="error" role="alert">{{ unassignedTriggerError }}</NAlert>
                </div>
              </NCollapseTransition>
            </div>
          </TaskConfigTransition>
        </div>
        <div class="task-form-section">
          <div class="task-section-heading task-actions-heading">
            <div>
              <h3>执行动作</h3>
              <p>动作按列表顺序执行</p>
            </div>
            <NButton attr-type="button" @click="editor.actions.push(createAction())">
              <template #icon><AppIcon name="plus" /></template>
              添加动作
            </NButton>
          </div>
          <TransitionGroup name="task-action">
            <div
              v-for="(action, index) in editor.actions"
              :key="actionKey(action)"
              class="task-action-transition"
            >
              <div class="task-action-content">
                <div class="task-action-spacing">
                  <NCard
                    tag="article"
                    :data-task-action="index"
                    class="task-action-card"
                    content-class="task-config-card"
                    embedded
                    size="small"
                  >
                    <div class="task-config-heading">
                      <strong>动作 {{ index + 1 }}</strong>
                      <div>
                        <NButton text :disabled="index === 0" @click="move(editor.actions, index, -1)">
                          上移
                        </NButton>
                        <NButton
                          text
                          :disabled="index === editor.actions.length - 1"
                          @click="move(editor.actions, index, 1)"
                        >
                          下移
                        </NButton>
                        <NButton
                          text
                          type="error"
                          :disabled="editor.actions.length === 1"
                          @click="editor.actions.splice(index, 1)"
                        >
                          删除
                        </NButton>
                      </div>
                    </div>
                    <div>
                      <TaskActionEditor
                        :action="action"
                        :tasks="tasks"
                        :tags="tags"
                        :registered-actions="registeredActions"
                        :extensions-loaded="extensionsLoaded"
                        :resource-state="actionResource(action.type)"
                        :load-resource="() => loadActionResource(action.type)"
                        :channel-options="channelOptions"
                        :variables-loading="variableHelpLoading"
                        :show-variables-loading="showVariableHelpLoading && variableHelpAction === action"
                        :error="actionErrors.get(action)?.message"
                        :invalid-field="invalidActionField(action)"
                        :load-options="(parameterKey) => loadActionOptions(index, parameterKey)"
                        @select-type="selectActionType(index, $event)"
                        @select-extension-action="selectExtensionAction(index, $event)"
                        @show-variables="openVariableHelp(action)"
                      />
                      <NCollapseTransition
                        :show="index < editor.actions.length - 1"
                        :inert="index >= editor.actions.length - 1 || undefined"
                      >
                        <div class="task-action-continue">
                          <NCheckbox v-model:checked="action.continueOnFailure">
                            动作失败后继续执行
                          </NCheckbox>
                        </div>
                      </NCollapseTransition>
                    </div>
                  </NCard>
                </div>
              </div>
            </div>
          </TransitionGroup>
        </div>
      </div>
      <template #actions>
        <NButton
          attr-type="button"
          :disabled="pendingOperations.has('save')"
          @click="editorModal.close()"
        >取消</NButton
        >
        <NButton
          :loading="pendingOperations.has('save')"
          attr-type="button"
          :disabled="pendingOperations.has('save')"
          @click="saveTask"
          type="primary"
        >{{ pendingOperations.has('save') ? '正在保存' : '保存' }}</NButton
        >
      </template>
    </AppModal>
    <TaskVariableHelp
      :show="variableHelpOpenModal.show"
      @after-leave="variableHelpOpenModal.afterLeave"
      v-if="variableHelpOpen"
      :variables="registeredVariables"
      @close="variableHelpOpenModal.close()"
    />
    <AppModal
      :show="detailTargetModal.show"
      @after-leave="detailTargetModal.afterLeave"
      v-if="detailTarget"
      :title="detailTarget.name"
      @close="detailTargetModal.close()"
    >
      <template v-if="detailDisplay?.warning" #title-extra>
        <AppTooltip :text="detailDisplay.warning">
          <NText
            type="warning"
            class="task-warning"
            role="img"
            :aria-label="detailDisplay.warning"
          >
            <AppIcon name="alert-circle" />
          </NText>
        </AppTooltip>
      </template>
      <p>{{ detailDisplay?.triggerLabel }}</p>
      <p v-for="(action, index) in detailTarget.actions" :key="index">
        {{ index + 1 }}. {{ actionLabel(action, index, detailTarget.actions.length) }}
      </p>
      <template #actions>
        <NButton
          :disabled="safeModeEnabled"
          @click="openTaskEditor(detailTarget)"
        >
          编辑
        </NButton>
        <NButton
          :disabled="safeModeEnabled"
          @click="
            deleteTargetModal.open(detailTarget);
            detailTargetModal.close();
          "
          type="error"
        >
          删除
        </NButton>
        <NButton @click="requestRun(detailTarget)" type="primary">立即运行</NButton>
      </template>
    </AppModal>
    <AppModal
      :show="runTargetModal.show"
      @after-leave="runTargetModal.afterLeave"
      :dismissible="!pendingOperations.has('run')"
      v-if="runTarget"
      title="确认运行任务"
      @close="runTargetModal.close()"
    >
      <p>确认立即运行任务“{{ runTarget.name }}”吗？</p>
      <template #actions>
        <NButton
          attr-type="button"
          :disabled="pendingOperations.has('run')"
          @click="runTargetModal.close()"
        >取消</NButton
        >
        <NButton
          :loading="pendingOperations.has('run')"
          :disabled="pendingOperations.has('run')"
          @click="runTask"
          type="primary"
        >{{ pendingOperations.has('run') ? '正在提交' : '运行' }}</NButton
        >
      </template>
    </AppModal>
    <AppModal
      :show="deleteTargetModal.show"
      @after-leave="deleteTargetModal.afterLeave"
      :dismissible="!pendingOperations.has('delete')"
      v-if="deleteTarget"
      title="确认删除任务"
      @close="deleteTargetModal.close()"
    >
      <p>确认删除任务“{{ deleteTarget.name }}”吗？</p>
      <template #actions>
        <NButton
          attr-type="button"
          :disabled="pendingOperations.has('delete')"
          @click="deleteTargetModal.close()"
        >取消</NButton
        >
        <NButton
          :loading="pendingOperations.has('delete')"
          :disabled="pendingOperations.has('delete')"
          @click="deleteTask"
          type="error"
        >{{ pendingOperations.has('delete') ? '正在删除' : '删除' }}</NButton
        >
      </template>
    </AppModal>
  </section>
</template>

<style>
.tag-search { width: min(100%, 360px); }
.tasks-view { display: grid; gap: var(--page-card-row-gap) 18px; }
.task-list { display: grid; align-content: start; gap: 0; }
.task-row, .task-form-section { transition: border-color .3s cubic-bezier(.4, 0, .2, 1); }
.task-list .content-empty { margin: 0; padding: 14px 0 4px; text-align: center; }
.task-list .content-section-header { padding-bottom: 8px; justify-content: flex-end; }
.task-list .content-section-header > .content-actions { width: 100%; flex-wrap: nowrap; align-items: center; justify-content: flex-end; }
.task-list .content-section-header > .content-actions:has(.tag-search) { justify-content: space-between; }
.task-list .tag-search { width: min(100%, 300px); }
.task-row { display: flex; min-height: 0; align-items: center; justify-content: space-between; gap: 22px; padding: 12px 0; border-top: 1px solid var(--border); }
.task-list .task-row:last-of-type { padding-bottom: 0; }
.task-row-main { display: grid; min-width: 0; flex: 1 1 auto; align-content: start; gap: 5px; }
.task-name-button { min-width: 0; max-width: 100%; height: auto; justify-content: flex-start; text-align: left; }
.task-name-copy { line-height: 1.5; overflow: hidden; text-overflow: ellipsis; white-space: inherit; }
.task-row-controls { display: flex; flex: none; align-items: center; gap: 10px; }
.task-warning { display: inline-flex; flex: none; }
.task-warning .app-icon { width: 16px; height: 16px; stroke-width: 2; transform: translateY(1px); }
.tasks-view:has(.task-form) > .status-message { position: fixed; z-index: 60; top: 78px; left: 50%; width: min(calc(100% - 40px), 520px); margin: 0; transform: translateX(-50%); }
.task-config-card { display: grid; gap: 12px; }
.task-action-spacing { padding-top: 12px; }
.task-action-continue { padding-top: 12px; }
.task-section-heading.task-actions-heading { margin-bottom: 0; }
.task-action-move { transition: transform .2s var(--n-bezier, cubic-bezier(.4, 0, .2, 1)); }
.task-config-heading, .task-section-heading { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.task-config-heading > div { display: flex; align-items: center; gap: 8px; }
.task-trigger-program-add[aria-invalid="true"] { border-color: var(--danger-border); }
.task-section-heading { margin-bottom: 12px; }
.task-section-heading h3 { margin: 0; font-size: 16px; }
.task-section-heading p { margin: 4px 0 0; color: var(--text-muted); font-size: 12px; transition: color .3s cubic-bezier(.4, 0, .2, 1); }
.task-form { display: grid; gap: 18px; }
.task-form-section { padding-top: 16px; border-top: 1px solid var(--border); }
.form-field.n-form-item > .form-field-content { display: grid; min-width: 0; grid-template-columns: minmax(0, 1fr); gap: 8px; }
.form-field.n-form-item > .form-field-content.task-trigger-programs { display: grid; gap: 0; margin-top: 10px; }
.task-trigger-path-spacing { padding-bottom: 10px; }
.task-trigger-program-controls { display: grid; gap: 6px; }
.task-trigger-program-controls .field-hint { margin: 0; }
.task-trigger-process { display: grid; margin-top: 12px; }
.task-trigger-program-reveal { padding-top: 12px; }
.task-trigger-extension { margin-top: 12px; }
.task-trigger-keyboard { margin-top: 12px; }
.task-trigger-program-add { width: fit-content; margin-top: 2px; }
@media (max-width: 767px) {
  .task-list .content-section-header > .content-actions { flex-wrap: wrap; }
  .task-list .tag-search { width: auto; flex: 1 1 180px; }
  .task-list .content-section-header > .content-actions > .n-button { flex-grow: 1; }
  .task-list .content-section-header { gap: 12px; }
  .task-row { display: grid; position: relative; align-items: center; grid-template-columns: minmax(0, 1fr) auto; gap: 14px; padding: 12px 0; }
  .task-row-main { padding-right: 0; }
  .task-name-button { overflow: visible; overflow-wrap: anywhere; text-overflow: clip; white-space: normal; }
  .task-section-heading { align-items: stretch; flex-direction: column; }
  .task-trigger-program-add { width: 100%; }
}
.task-config-heading > strong { font-weight: 500; }
.task-warning .n-icon.app-icon > svg { stroke-width: 2; }
@media (max-width: 767px) {
  .task-config-heading, .task-config-heading > div { flex-wrap: wrap; }
}
</style>
