export type Trigger = {
  type: 'Cron' | 'WinPilotStartup' | 'Process' | 'Extension' | 'Keyboard';
  event: '' | 'Started' | 'Exited';
  cron: string;
  processPaths: string[];
  keys: string[];
  pluginId?: string;
  triggerId?: string;
  parameters?: Record<string, any>;
};
export type RegisteredTrigger = {
  pluginId: string;
  pluginDisplayName: string;
  triggerId: string;
  triggerName: string;
  available: boolean;
  parameters: ParameterDefinition[];
};
export type ParameterDefinition = {
  key: string;
  label: string;
  type: 'Text' | 'Number' | 'Select' | 'Checkbox' | 'Textarea' | 'MultiCheckbox';
  required: boolean;
  defaultValue?: unknown;
  options?: { value: string; label: string }[];
};
export type RegisteredAction = {
  pluginId: string;
  pluginDisplayName: string;
  actionId: string;
  actionName: string;
  available: boolean;
  dynamicOptions?: boolean;
  parameters: ParameterDefinition[];
};
export type RegisteredVariable = {
  name: string;
  displayName: string;
  description: string;
  type: 'Text' | 'Image';
  pluginId: string;
  pluginDisplayName: string;
};
export type Action = {
  type:
    | 'StartProgram'
    | 'StartTag'
    | 'CloseProcess'
    | 'Delay'
    | 'Keyboard'
    | 'Shell'
    | 'SendNotification'
    | 'TaskState'
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
  taskName: string;
  state: '' | 'Toggle' | 'Enable' | 'Disable';
  pluginId?: string;
  actionId?: string;
  parameters?: Record<string, any>;
  continueOnFailure: boolean;
};
export type Task = {
  name: string;
  enabled: boolean;
  trigger: Trigger;
  actions: Action[];
};
export type ListedTask = Task & { cronAvailable?: boolean; keyboardAvailable?: boolean };

export type ActionOption = { value: string; label: string };

export const actionOptions = [
  ['StartProgram', '启动'],
  ['StartTag', '快捷标签'],
  ['CloseProcess', '关闭进程'],
  ['Delay', '延迟'],
  ['Shell', 'Shell'],
  ['Keyboard', '按键模拟'],
  ['SendNotification', '发送通知'],
  ['TaskState', '任务状态'],
  ['Extension', '插件扩展'],
] as const;
export const taskStateOptions = [
  { value: '', label: '请选择状态' },
  { value: 'Toggle', label: '切换' },
  { value: 'Enable', label: '启用' },
  { value: 'Disable', label: '禁用' },
];
export const createTrigger = (): Trigger => ({
  type: 'Cron',
  cron: '',
  event: '',
  processPaths: [],
  keys: [],
  pluginId: '',
  triggerId: '',
  parameters: {},
});

export const createAction = (): Action => ({
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
  delaySeconds: 5,
  keys: [],
  shell: 'PowerShell',
  command: '',
  notificationTitle: '',
  notificationContent: '',
  notificationChannel: '',
  taskName: '',
  state: '',
  pluginId: '',
  actionId: '',
  parameters: {},
  continueOnFailure: false,
});
export function parameterDefaults(parameters: ParameterDefinition[] = []) {
  return Object.fromEntries(parameters.map(parameter => [
    parameter.key,
    parameter.defaultValue == null
      ? (parameter.type === 'MultiCheckbox' ? [] : parameter.type === 'Checkbox' ? false : '')
      : JSON.parse(JSON.stringify(parameter.defaultValue)),
  ]));
}

const actionFields: Record<Action['type'], readonly (keyof Action)[]> = {
  StartProgram: ['programPath', 'arguments', 'workingDirectory', 'runElevated', 'silent'],
  StartTag: ['tagName'],
  CloseProcess: ['processName', 'force', 'currentUserOnly'],
  Delay: ['delaySeconds'],
  Keyboard: ['keys'],
  Shell: ['shell', 'command'],
  SendNotification: ['notificationTitle', 'notificationContent', 'notificationChannel'],
  TaskState: ['taskName', 'state'],
  Extension: ['pluginId', 'actionId', 'parameters'],
};
export function multiSelectionLabel(value: unknown) {
  if (!Array.isArray(value) || value.length === 0) return '请选择配置';
  return `已选择 ${value.length} 项`;
}
export function multiParameterOptions(parameter: ParameterDefinition, value: unknown) {
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
export function toggleMultiOption(target: Pick<Action, 'parameters'>, parameterKey: string, value: string, checked: boolean) {
  target.parameters ??= {};
  const selected = Array.isArray(target.parameters[parameterKey])
    ? (target.parameters[parameterKey] as string[])
    : [];
  const position = selected.indexOf(value);
  if (!checked && position >= 0) selected.splice(position, 1);
  else if (checked && position < 0) selected.push(value);
  target.parameters[parameterKey] = selected;
}

export function actionPayload(action: Action) {
  return {
    type: action.type,
    ...Object.fromEntries(actionFields[action.type].map(key => [key, action[key]])),
    ...(action.continueOnFailure ? { continueOnFailure: true } : {}),
  };
}
