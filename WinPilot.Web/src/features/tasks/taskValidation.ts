import type { Task, ParameterDefinition, RegisteredTrigger, RegisteredAction } from './taskModels';
import { taskStateOptions } from './taskModels';

export function unavailableSelectValue(parameter: ParameterDefinition, value: unknown) {
  const selected = String(value ?? '');
  return selected && !parameter.options?.some((option) => option.value === selected) ? selected : '';
}
function isRequiredParameterMissing(parameter: ParameterDefinition, value: unknown) {
  if (!parameter.required) return false;
  if (parameter.type === 'MultiCheckbox') return !Array.isArray(value) || value.length === 0;
  if (parameter.type === 'Checkbox') return false;
  return value === null || value === undefined || !String(value).trim();
}
export function hasUnavailableParameter(parameter: ParameterDefinition, value: unknown) {
  if (parameter.type === 'Select') return Boolean(unavailableSelectValue(parameter, value));
  if (parameter.type !== 'MultiCheckbox' || !Array.isArray(value)) return false;
  return value
    .map(String)
    .some((selected) => !parameter.options?.some((option) => option.value === selected));
}
export function invalidParameter(definitions: ParameterDefinition[] = [], values?: Record<string, unknown>) {
  return definitions.find(parameter => isRequiredParameterMissing(parameter, values?.[parameter.key]))
    ?? definitions.find(parameter => hasUnavailableParameter(parameter, values?.[parameter.key]));
}
function parameterValidationError(definitions: ParameterDefinition[] = [], values?: Record<string, unknown>) {
  const parameter = invalidParameter(definitions, values);
  if (!parameter) return '';
  if (isRequiredParameterMissing(parameter, values?.[parameter.key]))
    return parameter.type === 'Select' || parameter.type === 'MultiCheckbox'
      ? `请选择${parameter.label}`
      : `请输入${parameter.label}`;
  return `${parameter.label}包含不可用配置，请重新选择`;
}
export function validateTask(value: Task, registeredTriggers: readonly RegisteredTrigger[], registeredActions: readonly RegisteredAction[]) {
  const result: Record<string, string> = {};
  if (!value.name.trim()) result.name = '请输入任务名称';
  else if (/[<>:"/\\|?*\x00-\x1f]/.test(value.name.trim()) || value.name.trim().endsWith('.'))
    result.name = '任务名称无效';
  const trigger = value.trigger;
  if (trigger.type === 'Cron' && !trigger.cron.trim()) result.cron = '请输入 Cron 表达式';
  if (trigger.type === 'Keyboard') {
    if (!trigger.keys.length) result.keys = '请选择至少一个按键';
    else if (trigger.keys.length > 3) result.keys = '最多选择 3 个按键';
  }
  if (trigger.type === 'Process') {
    if (trigger.event !== 'Started' && trigger.event !== 'Exited') result.event = '请选择事件';
    const paths = trigger.processPaths.map((path) => path.trim()).filter(Boolean);
    if (!paths.length) result.processPaths = '请至少选择一个程序绝对路径';
    else if (paths.some((path) => !/^(?:[a-zA-Z]:[\\/]|\\\\)/.test(path)))
      result.processPaths = '程序路径必须是绝对路径';
  }
  if (trigger.type === 'Extension' && (!trigger.pluginId?.trim() || !trigger.triggerId?.trim()))
    result.extension = '请选择插件和触发条件';
  if (trigger.type === 'Extension' && trigger.pluginId && trigger.triggerId) {
    const definition = registeredTriggers.find(
      (item) => item.pluginId === trigger.pluginId && item.triggerId === trigger.triggerId,
    );
    const error = parameterValidationError(definition?.parameters, trigger.parameters);
    if (error) result.extension = error;
  }
  value.actions.forEach((action, index) => {
    if (action.type === 'Extension' && (!action.pluginId?.trim() || !action.actionId?.trim()))
      result[`action-${index}`] = '请选择插件和动作';
    if (action.type === 'Extension' && action.pluginId && action.actionId) {
      const definition = registeredActions.find(
        (item) => item.pluginId === action.pluginId && item.actionId === action.actionId,
      );
      const error = parameterValidationError(definition?.parameters, action.parameters);
      if (error) result[`action-${index}`] = error;
    }
    if (action.type === 'StartProgram' && !action.programPath.trim())
      result[`action-${index}`] = '请选择要启动的程序';
    if (action.type === 'StartTag' && !action.tagName.trim())
      result[`action-${index}`] = '请选择快捷标签';
    if (action.type === 'EndProcess' && !action.processName.trim())
      result[`action-${index}`] = '请输入进程名';
    if (action.type === 'Delay' && (!Number.isInteger(action.delaySeconds) || Number(action.delaySeconds) <= 0))
      result[`action-${index}`] = '请输入有效的延迟秒数';
    else if (action.type === 'Delay' && Number(action.delaySeconds) > 86400)
      result[`action-${index}`] = '延迟时间不能超过 86400 秒（24 小时）';
    if (action.type === 'Keyboard' && !action.keys.length) result[`action-${index}`] = '请选择至少一个按键';
    if (action.type === 'Shell' && !action.command.trim()) result[`action-${index}`] = '请输入 Shell 命令';
    if (action.type === 'SendNotification' && !action.notificationContent.trim())
      result[`action-${index}`] = '请输入通知内容';
    else if (action.type === 'SendNotification' && !action.notificationChannel.trim())
      result[`action-${index}`] = '请选择发送渠道';
    if (action.type === 'TaskState') {
      if (!action.taskName.trim()) result[`action-${index}`] = '请选择任务';
      else if (!taskStateOptions.some(option => option.value !== '' && option.value === action.state))
        result[`action-${index}`] = '请选择状态';
    }
  });
  return result;
}
