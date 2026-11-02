import { keyLabel, orderKeys } from '../../ui/keyboardLayout';
import {
  taskStateOptions,
  type Action,
  type ActionOption,
  type ListedTask,
  type RegisteredAction,
  type RegisteredTrigger,
} from './taskModels';
import { hasUnavailableParameter } from './taskValidation';

export interface TaskPresentationContext {
  triggers: RegisteredTrigger[];
  actions: RegisteredAction[];
  extensionsLoaded: boolean;
  actionOptions: ReadonlyMap<string, ActionOption[]>;
}

export function actionOptionsKey(pluginId: string, actionId: string, parameterKey: string) {
  return JSON.stringify([pluginId, actionId, parameterKey]);
}

export function describeTask(task: ListedTask, context: TaskPresentationContext) {
  const trigger = task.trigger;
  let label: string;
  let triggerWarning = '';
  switch (trigger.type) {
    case 'Cron':
      label = `Cron 计划 · ${trigger.cron || '未设置'}`;
      if (task.cronAvailable === false) triggerWarning = 'Cron 表达式没有后续执行时间';
      break;
    case 'Keyboard':
      label = `按键监听 · ${
        orderKeys(trigger.keys ?? [])
          .map(keyLabel)
          .join(' + ') || '未选择按键'
      }`;
      if (task.enabled && task.keyboardAvailable === false) triggerWarning = '按键监听不可用';
      break;
    case 'WinPilotStartup':
      label = 'WinPilot 启动';
      break;
    case 'Extension': {
      const definition = context.triggers.find(
        (item) => item.pluginId === trigger.pluginId && item.triggerId === trigger.triggerId,
      );
      const parameter = definition?.parameters?.find(
        (item) =>
          item.type === 'Select' &&
          item.options?.some(
            (option) => option.value === String(trigger.parameters?.[item.key] ?? ''),
          ),
      );
      const selected = parameter?.options?.find(
        (option) => option.value === String(trigger.parameters?.[parameter.key] ?? ''),
      );
      const pluginName = definition?.pluginDisplayName || trigger.pluginId || '未选择插件';
      const triggerName = definition?.triggerName || trigger.triggerId || '未选择配置';
      label = `扩展 · ${pluginName} · ${triggerName}${selected ? ` - ${selected.label}` : ''}`;
      if (
        context.extensionsLoaded &&
        trigger.pluginId &&
        trigger.triggerId &&
        (!definition?.available ||
          definition.parameters?.some((item) =>
            hasUnavailableParameter(item, trigger.parameters?.[item.key]),
          ))
      )
        triggerWarning = '插件触发条件不可用';
      break;
    }
    default:
      label = `程序事件 · ${trigger.event === 'Started' ? '程序启动时' : trigger.event === 'Exited' ? '程序退出时' : '未选择事件'}`;
  }
  const warnings: string[] = [];
  if (triggerWarning) warnings.push(triggerWarning);
  if (
    task.actions.some((action) => {
      if (!context.extensionsLoaded || action.type !== 'Extension') return false;
      const definition = context.actions.find(item => item.pluginId === action.pluginId && item.actionId === action.actionId);
      if (!definition?.available) return true;
      return (
        !definition.dynamicOptions &&
        definition.parameters.some((parameter) =>
          hasUnavailableParameter(parameter, action.parameters?.[parameter.key]),
        )
      );
    })
  )
    warnings.push('部分插件动作不可用');
  return {
    triggerLabel: label + (triggerWarning ? '（不可用）' : ''),
    warning: warnings.join('\n'),
  };
}
export function actionCoreLabel(action: Action, context: TaskPresentationContext): string {
  switch (action.type) {
    case 'Extension':
      return extensionActionLabel(action, context);
    case 'Delay':
      return `延迟 ${action.delaySeconds || 0} 秒`;
    case 'Shell':
      return `Shell · ${action.shell} · ${action.command || '未设置命令'}`;
    case 'Keyboard':
      return `按键模拟 · ${orderKeys(action.keys).join(' + ') || '未设置'}`;
    case 'StartTag':
      return `启动快捷标签 · ${action.tagName || '未选择'}`;
    case 'EndProcess':
      return `结束进程 · ${action.processName || '未指定'}${action.currentUserOnly ? ' · 当前用户' : ''}`;
    case 'SendNotification':
      return `发送通知 · ${action.notificationContent || '未设置'}`;
    case 'TaskState':
      return `任务状态 · ${taskStateOptions.find((option) => option.value === action.state)?.label ?? '请选择状态'} · ${action.taskName || '未选择任务'}`;
    default:
      return `启动程序 · ${action.programPath || '未指定'}`;
  }
}
export const actionLabel = (action: Action, index: number, total: number, context: TaskPresentationContext) =>
  `${actionCoreLabel(action, context)}${action.continueOnFailure && index < total - 1 ? ' · 失败后继续' : ''}`;
export function extensionActionLabel(action: Action, context: TaskPresentationContext) {
  const definition = context.actions.find(item => item.pluginId === action.pluginId && item.actionId === action.actionId);
  if (!definition)
    return `扩展 · ${action.pluginId || '未选择插件'} · ${action.actionId || '未选择动作'}${context.extensionsLoaded && action.pluginId && action.actionId ? '（不可用）' : ''}`;
  if (!definition.available) return `${definition.actionName}（不可用）`;
  const labels = definition.parameters.flatMap((parameter) => {
    const options = definition.dynamicOptions
      ? context.actionOptions.get(
          actionOptionsKey(definition.pluginId, definition.actionId, parameter.key),
        )
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
