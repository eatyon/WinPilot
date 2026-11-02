import { orderKeys } from '../../ui/keyboardLayout';
import { actionPayload, type Action, type Task } from './taskModels';

// 规范化提交副本，不直接改变编辑草稿；最后一个动作没有后续动作，因此清除继续执行开关。
export function normalizeTask(task: Task): Task {
  const value = JSON.parse(JSON.stringify(task)) as Task;
  value.name = value.name.trim();
  value.trigger.processPaths = value.trigger.processPaths.map(path => path.trim()).filter(Boolean);
  if (value.trigger.type === 'Keyboard') value.trigger.keys = orderKeys(value.trigger.keys);
  for (const action of value.actions)
    if (action.type === 'Keyboard') action.keys = orderKeys(action.keys);
  if (value.actions.length) value.actions[value.actions.length - 1].continueOnFailure = false;
  return value;
}

export function taskPayload(task: Task) {
  return {
    ...task,
    trigger: {
      ...task.trigger,
      event: task.trigger.type === 'Process' ? task.trigger.event : undefined,
      keys: task.trigger.type === 'Keyboard' ? task.trigger.keys : undefined,
    },
    actions: task.actions.map(actionPayload),
  };
}

// 保存动作对象身份和提交字段，排序后仍能定位原动作，内容已变更或动作已删除时不再回填错误。
export function snapshotTask(task: Task) {
  return {
    name: task.name,
    trigger: JSON.stringify(task.trigger),
    actions: task.actions.map(action => ({ action, snapshot: JSON.stringify(actionPayload(action)) })),
  };
}

const triggerFields: Record<string, string> = {
  'trigger.cron': 'cron',
  'trigger.event': 'event',
  'trigger.processPaths': 'processPaths',
  'trigger.keys': 'keys',
  'trigger.pluginId': 'extension',
  'trigger.triggerId': 'extension',
  'trigger.parameters': 'extension',
};

// 仅把后端错误应用到仍与提交快照一致的字段，避免旧请求的红字落到已变更内容上。
export function mapTaskSaveErrors(problem: unknown, task: Task, submitted: ReturnType<typeof snapshotTask>) {
  const errors = problem && typeof problem === 'object' && !Array.isArray(problem)
    ? (problem as Record<string, unknown>).errors
    : undefined;
  if (!errors || typeof errors !== 'object' || Array.isArray(errors)) return;

  const fields: Record<string, string> = {};
  const actions = new Map<Action, { message: string; snapshot: string; field: string }>();
  let triggerErrorField = '';
  for (const [path, messages] of Object.entries(errors)) {
    if (!Array.isArray(messages)) continue;
    const message = messages.find(value => typeof value === 'string' && value.trim());
    if (!message) continue;
    const triggerField = triggerFields[path.split(/[.\[\]]/).slice(0, 2).join('.')];
    const actionIndex = path.match(/^actions\[(\d+)\](?:\.|$)/)?.[1];
    const submittedAction = actionIndex === undefined ? undefined : submitted.actions[Number(actionIndex)];
    if (path === 'name' && task.name === submitted.name) {
      fields.name = message;
    } else if (triggerField && JSON.stringify(task.trigger) === submitted.trigger) {
      fields[triggerField] = message;
      if (triggerField === 'extension' && !triggerErrorField)
        triggerErrorField = path.replace(/^trigger\./, '');
    } else if (
      submittedAction && task.actions.includes(submittedAction.action) &&
      JSON.stringify(actionPayload(submittedAction.action)) === submittedAction.snapshot
    ) {
      actions.set(submittedAction.action, {
        message,
        snapshot: submittedAction.snapshot,
        field: path.replace(/^actions\[\d+\]\.?/, ''),
      });
    }
  }
  return { fields, actions, triggerErrorField };
}
