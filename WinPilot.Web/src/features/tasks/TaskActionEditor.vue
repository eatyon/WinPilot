<script setup lang="ts">
import AppTooltip from '../../components/AppTooltip.vue';
import TaskConfigTransition from './TaskConfigTransition.vue';
import { NAlert, NButton, NCheckbox, NCollapseTransition, NFormItem, NInput } from 'naive-ui';
import AppNumberInput from '../../components/AppNumberInput.vue';

import { useId, computed } from 'vue';
import AppIcon from '../../components/AppIcon.vue';
import AppSelect from '../../components/AppSelect.vue';
import AppMultiSelect from '../../components/AppMultiSelect.vue';
import KeyboardKeyPicker from '../../components/KeyboardKeyPicker.vue';
import {
  actionOptions,
  taskStateOptions,
  multiSelectionLabel,
  multiParameterOptions,
  toggleMultiOption,
  type Action,
  type Task,
  type RegisteredAction,
  type ActionOption,
} from './taskModels';
import { unavailableSelectValue } from './taskValidation';

const formId = useId();

const props = defineProps<{
  action: Action;
  tasks: readonly Pick<Task, 'name'>[];
  tags: readonly { name: string }[];
  registeredActions: readonly RegisteredAction[];
  extensionsLoaded: boolean;
  channelOptions: readonly ActionOption[];
  variablesLoading: boolean;
  showVariablesLoading: boolean;
  resourceState?: { loading: boolean; error: string };
  loadResource: () => Promise<boolean>;
  error?: string;
  invalidField?: string;
  loadOptions: (parameterKey: string) => Promise<boolean>;
}>();
const emit = defineEmits<{
  'select-type': [Action['type']];
  'select-extension-action': [string];
  'show-variables': [];
}>();
const actionPlugins = computed(() => [
  ...new Map(props.registeredActions.map((item) => [item.pluginId, item])).values(),
]);
const actionDefinitions = computed(() =>
  props.registeredActions.filter((item) => item.pluginId === props.action.pluginId),
);
const actionDefinition = computed(() =>
  actionDefinitions.value.find((item) => item.actionId === props.action.actionId),
);
const feedbackFields = computed(() => {
  switch (props.action.type) {
    case 'Extension':
      return ['type', 'pluginId', 'actionId', ...(actionDefinition.value?.parameters.map(parameter => `parameters.${parameter.key}`) ?? [])];
    case 'StartProgram': return ['type', 'programPath', 'arguments', 'workingDirectory'];
    case 'StartTag': return ['type', 'tagName'];
    case 'TaskState': return ['type', 'taskName', 'state'];
    case 'EndProcess': return ['type', 'processName'];
    case 'Delay': return ['type', 'delaySeconds'];
    case 'Shell': return ['type', 'shell', 'command'];
    case 'SendNotification': return ['type', 'notificationContent', 'notificationChannel'];
    case 'Keyboard': return ['type', 'keys'];
    default: return ['type'];
  }
});
function fieldError(field: string) {
  return props.invalidField === field ? props.error : undefined;
}
const unassignedError = computed(() =>
  props.error && !feedbackFields.value.includes(props.invalidField ?? '') ? props.error : undefined,
);
</script>

<template>
  <div class="task-action-editor">
    <NFormItem
      :show-label="false"
      :show-feedback="Boolean(fieldError('type'))"
      :validation-status="fieldError('type') ? 'error' : undefined"
      :feedback="fieldError('type')"
    >
      <AppSelect
        data-task-field="type"
        :model-value="action.type"
        :invalid="invalidField === 'type'"
        :options="actionOptions.map(([value, label]) => ({ value, label }))"
        @change="$event && emit('select-type', $event)"
      />
    </NFormItem>
    <TaskConfigTransition :value="action.type">
      <div class="task-action-fields task-action-spacing">
        <template v-if="action.type === 'Extension'">
          <NFormItem
            label="插件"
            required
            :label-props="{ id: (`${formId}-field-0`) + '-label' }"
            :show-feedback="Boolean(fieldError('pluginId'))"
            :validation-status="fieldError('pluginId') ? 'error' : undefined"
            :feedback="fieldError('pluginId')"
            class="form-field"
            data-task-field="pluginId"
          >
            <AppSelect
              :labelled-by="(`${formId}-field-0`) + '-label'"
              :id="`${formId}-field-0`"
              v-model="action.pluginId"
              :disabled="!extensionsLoaded"
              :invalid="invalidField === 'pluginId'"
              :options="[
                ...actionPlugins.map((plugin) => ({
                  value: plugin.pluginId,
                  label: plugin.pluginDisplayName,
                })),
                ...(action.pluginId && !actionPlugins.some((item) => item.pluginId === action.pluginId)
                  ? [
                      {
                        value: action.pluginId,
                        label: `${action.pluginId}${extensionsLoaded ? '（不可用）' : ''}`,
                      },
                    ]
                  : []),
              ]"
              @change="
                action.actionId = '';
                action.parameters = {};
              "
            />
          </NFormItem>
          <NFormItem
            label="动作"
            required
            :label-props="{ id: (`${formId}-field-1`) + '-label' }"
            :show-feedback="Boolean(fieldError('actionId'))"
            :validation-status="fieldError('actionId') ? 'error' : undefined"
            :feedback="fieldError('actionId')"
            class="form-field"
            data-task-field="actionId"
          >
            <AppSelect
              :labelled-by="(`${formId}-field-1`) + '-label'"
              :id="`${formId}-field-1`"
              :model-value="action.actionId || ''"
              :disabled="!extensionsLoaded"
              :invalid="invalidField === 'actionId'"
              :options="[
                ...actionDefinitions.map((definition) => ({
                  value: definition.actionId,
                  label: definition.actionName,
                })),
                ...(action.actionId &&
                !actionDefinitions.some((item) => item.actionId === action.actionId)
                  ? [
                      {
                        value: action.actionId,
                        label: `${action.actionId}${extensionsLoaded ? '（不可用）' : ''}`,
                      },
                    ]
                  : []),
              ]"
              @change="emit('select-extension-action', $event)"
            />
          </NFormItem>
          <template v-if="action.actionId && actionDefinition">
            <NFormItem
              :required="parameter.required"
              :label-props="{ id: (`${formId}-field-2-${parameter.key}`) + '-label' }"
              :show-label="parameter.type !== 'Checkbox'"
              :show-feedback="Boolean(fieldError(`parameters.${parameter.key}`))"
              :validation-status="fieldError(`parameters.${parameter.key}`) ? 'error' : undefined"
              :feedback="fieldError(`parameters.${parameter.key}`)"
              v-for="parameter in actionDefinition.parameters"
              :key="parameter.key"
              :data-task-field="`parameters.${parameter.key}`"
              class="form-field"
            >
              <template #label>{{ parameter.label }}</template>
              <NInput
                v-if="parameter.type === 'Textarea'"
                :status="
                  invalidField === `parameters.${parameter.key}` || undefined ? 'error' : undefined
                "
                v-model:value="action.parameters![parameter.key] as string | null | undefined"
                :input-props="{ id: `${formId}-field-2-${parameter.key}`,
                  'aria-invalid': invalidField === `parameters.${parameter.key}` || undefined,
                }"
                type="textarea"
              ></NInput>
              <AppNumberInput
                :id="`${formId}-field-2-${parameter.key}`"
                v-else-if="parameter.type === 'Number'"
                :aria-invalid="invalidField === `parameters.${parameter.key}` || undefined"
                v-model="action.parameters![parameter.key] as number | string | null | undefined"
              />
              <AppSelect
                :labelled-by="(`${formId}-field-2-${parameter.key}`) + '-label'"
                :id="`${formId}-field-2-${parameter.key}`"
                v-else-if="parameter.type === 'Select'"
                :placeholder="parameter.options?.length ? undefined : '暂无可用配置'"
                :invalid="invalidField === `parameters.${parameter.key}`"
                v-model="action.parameters![parameter.key] as string | number | null | undefined"
                :before-open="() => loadOptions(parameter.key)"
                :disabled="
                  !actionDefinition.dynamicOptions &&
                  !parameter.options?.length &&
                  !unavailableSelectValue(parameter, action.parameters?.[parameter.key])
                "
                :options="[
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
              <NCheckbox
                v-else-if="parameter.type === 'Checkbox'"
                v-model:checked="action.parameters![parameter.key] as boolean"
                :aria-invalid="invalidField === `parameters.${parameter.key}` || undefined"
              >{{ parameter.label }}</NCheckbox
            >
              <AppMultiSelect
                :labelled-by="(`${formId}-field-2-${parameter.key}`) + '-label'"
                :id="`${formId}-field-2-${parameter.key}`"
                v-else-if="parameter.type === 'MultiCheckbox'"
                :invalid="invalidField === `parameters.${parameter.key}`"
                :before-open="() => loadOptions(parameter.key)"
                :model-value="
                  Array.isArray(action.parameters?.[parameter.key])
                    ? action.parameters![parameter.key] as string[]
                    : []
                "
                :options="multiParameterOptions(parameter, action.parameters?.[parameter.key])"
                :disabled="
                  !actionDefinition.dynamicOptions &&
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
              <NInput
                v-else
                v-model:value="action.parameters![parameter.key] as string | null | undefined"
                type="text"
                :status="
                  invalidField === `parameters.${parameter.key}` || undefined ? 'error' : undefined
                "
                :input-props="{ id: `${formId}-field-2-${parameter.key}`,
                  'aria-invalid': invalidField === `parameters.${parameter.key}` || undefined,
                }"
              />
            </NFormItem>
          </template>
        </template>
        <template v-else-if="action.type === 'StartProgram'">
          <NFormItem
            label="程序 / 脚本路径"
            required
            :label-props="{ for: `${formId}-field-3` }"
            :show-feedback="Boolean(fieldError('programPath'))"
            :validation-status="fieldError('programPath') ? 'error' : undefined"
            :feedback="fieldError('programPath')"
            class="form-field"
            data-task-field="programPath"
          >
            <NInput
              v-model:value="action.programPath"
              placeholder="请输入绝对路径"
              :status="invalidField === 'programPath' || undefined ? 'error' : undefined"
              :input-props="{ id: `${formId}-field-3`, 'aria-invalid': invalidField === 'programPath' || undefined }"
            />
          </NFormItem>
          <NFormItem
            label="参数"
            :label-props="{ for: `${formId}-field-4` }"
            :show-feedback="Boolean(fieldError('arguments'))"
            :validation-status="fieldError('arguments') ? 'error' : undefined"
            :feedback="fieldError('arguments')"
            class="form-field"
            data-task-field="arguments"
          >
            <NInput
              v-model:value="action.arguments"
              placeholder="可留空"
              :status="invalidField === 'arguments' || undefined ? 'error' : undefined"
              :input-props="{ id: `${formId}-field-4`, 'aria-invalid': invalidField === 'arguments' || undefined }"
            />
          </NFormItem>
          <NFormItem
            label="起始目录"
            :label-props="{ for: `${formId}-field-5` }"
            :show-feedback="Boolean(fieldError('workingDirectory'))"
            :validation-status="fieldError('workingDirectory') ? 'error' : undefined"
            :feedback="fieldError('workingDirectory')"
            class="form-field"
            data-task-field="workingDirectory"
          >
            <NInput
              v-model:value="action.workingDirectory"
              placeholder="可留空"
              :status="invalidField === 'workingDirectory' || undefined ? 'error' : undefined"
              :input-props="{ id: `${formId}-field-5`, 'aria-invalid': invalidField === 'workingDirectory' || undefined }"
            />
          </NFormItem>
          <div class="task-process-options">
            <NCheckbox v-model:checked="action.runElevated">使用最高权限运行</NCheckbox>
            <NCheckbox v-model:checked="action.silent">静默执行</NCheckbox>
          </div>
        </template>
        <NFormItem
          label="标签"
          required
          :label-props="{ id: (`${formId}-field-6`) + '-label' }"
          :show-feedback="Boolean(fieldError('tagName') || resourceState?.loading || resourceState?.error)"
          :validation-status="fieldError('tagName') || (!resourceState?.loading && resourceState?.error) ? 'error' : undefined"
          v-else-if="action.type === 'StartTag'"
          class="form-field"
          data-task-field="tagName"
        >
          <AppSelect
            :labelled-by="(`${formId}-field-6`) + '-label'"
            :id="`${formId}-field-6`"
            v-model="action.tagName"
            :before-open="loadResource"
            :disabled="resourceState?.loading"
            :invalid="invalidField === 'tagName'"
            :options="[
              ...tags.map((tag) => ({ value: tag.name, label: tag.name })),
              ...(action.tagName && !tags.some((tag) => tag.name === action.tagName)
                ? [{ value: action.tagName, label: action.tagName }]
                : []),
            ]"
          />


          <template #feedback>
            <span :role="!fieldError('tagName') && resourceState?.loading ? 'status' : 'alert'">{{
            fieldError('tagName') || (resourceState?.loading ? '正在加载快捷标签…' : resourceState?.error + '，可展开选择器重试')
          }}</span>
          </template>
        </NFormItem>
        <template v-else-if="action.type === 'TaskState'">
          <NFormItem
            label="任务"
            required
            :label-props="{ id: (`${formId}-field-7`) + '-label' }"
            :show-feedback="Boolean(fieldError('taskName'))"
            :validation-status="fieldError('taskName') ? 'error' : undefined"
            :feedback="fieldError('taskName')"
            class="form-field"
            data-task-field="taskName"
          >
            <AppSelect
              :labelled-by="(`${formId}-field-7`) + '-label'"
              :id="`${formId}-field-7`"
              v-model="action.taskName"
              :invalid="invalidField === 'taskName'"
              :options="[
                ...tasks.map((task) => ({ value: task.name, label: task.name })),
                ...(action.taskName && !tasks.some((task) => task.name === action.taskName)
                  ? [{ value: action.taskName, label: `${action.taskName}（不存在）` }]
                  : []),
              ]"
            />
          </NFormItem>
          <NFormItem
            label="状态"
            required
            :label-props="{ id: (`${formId}-field-8`) + '-label' }"
            :show-feedback="Boolean(fieldError('state'))"
            :validation-status="fieldError('state') ? 'error' : undefined"
            :feedback="fieldError('state')"
            class="form-field"
            data-task-field="state"
          >
            <AppSelect
              :labelled-by="(`${formId}-field-8`) + '-label'"
              :id="`${formId}-field-8`"
              v-model="action.state"
              :invalid="invalidField === 'state'"
              :options="taskStateOptions"
            />
          </NFormItem>
        </template>
        <template v-else-if="action.type === 'EndProcess'">
          <NFormItem
            label="进程名"
            required
            :label-props="{ for: `${formId}-field-9` }"
            :show-feedback="Boolean(fieldError('processName'))"
            :validation-status="fieldError('processName') ? 'error' : undefined"
            :feedback="fieldError('processName')"
            class="form-field"
            data-task-field="processName"
          >
            <NInput
              v-model:value="action.processName"
              :status="invalidField === 'processName' || undefined ? 'error' : undefined"
              :input-props="{ id: `${formId}-field-9`, 'aria-invalid': invalidField === 'processName' || undefined }"
            />
          </NFormItem>
          <div class="task-process-options">
            <NCheckbox v-model:checked="action.currentUserOnly">当前用户</NCheckbox>
          </div>
        </template>
        <NFormItem
          label="秒数"
          required
          :label-props="{ for: `${formId}-field-10` }"
          :show-feedback="Boolean(fieldError('delaySeconds'))"
          :validation-status="fieldError('delaySeconds') ? 'error' : undefined"
          :feedback="fieldError('delaySeconds')"
          v-else-if="action.type === 'Delay'"
          class="form-field"
          data-task-field="delaySeconds"
        >
          <AppNumberInput
            :id="`${formId}-field-10`"
            v-model="action.delaySeconds"
            :aria-invalid="invalidField === 'delaySeconds' || undefined"
            placeholder="请输入 1–86400 之间的整数"
            min="1"
            max="86400"
            step="1"
            title=""
            aria-label="秒数"
          />
        </NFormItem>
        <template v-else-if="action.type === 'Shell'">
          <NFormItem
            label="终端类型"
            required
            :label-props="{ id: (`${formId}-field-11`) + '-label' }"
            :show-feedback="Boolean(fieldError('shell'))"
            :validation-status="fieldError('shell') ? 'error' : undefined"
            :feedback="fieldError('shell')"
            class="form-field"
            data-task-field="shell"
          >
            <AppSelect
              :labelled-by="(`${formId}-field-11`) + '-label'"
              :id="`${formId}-field-11`"
              v-model="action.shell"
              :invalid="invalidField === 'shell'"
              :options="[
                { value: 'PowerShell', label: 'PowerShell' },
                { value: 'CMD', label: 'CMD' },
              ]"
            />
          </NFormItem>
          <NFormItem
            label="命令"
            required
            :label-props="{ for: `${formId}-field-12` }"
            :show-feedback="Boolean(fieldError('command'))"
            :validation-status="fieldError('command') ? 'error' : undefined"
            :feedback="fieldError('command')"
            class="form-field"
            data-task-field="command"
          >
            <NInput
              v-model:value="action.command"
              :status="invalidField === 'command' || undefined ? 'error' : undefined"
              :input-props="{ id: `${formId}-field-12`, 'aria-invalid': invalidField === 'command' || undefined }"
            />
          </NFormItem>
        </template>
        <template v-else-if="action.type === 'SendNotification'">
          <div class="notification-content-field">
            <NFormItem
              label="通知内容"
              required
              :label-props="{ for: `${formId}-field-13` }"
              :show-feedback="Boolean(fieldError('notificationContent'))"
              :validation-status="fieldError('notificationContent') ? 'error' : undefined"
              :feedback="fieldError('notificationContent')"
              class="form-field"
              data-task-field="notificationContent"
            >
              <NInput
                v-model:value="action.notificationContent"
                :status="invalidField === 'notificationContent' || undefined ? 'error' : undefined"
                :input-props="{ id: `${formId}-field-13`,
                  'aria-invalid': invalidField === 'notificationContent' || undefined,
                }"
                type="textarea"
              ></NInput>
            </NFormItem>
            <AppTooltip text="查看可用变量">
              <NButton
                quaternary
                class="app-icon-button notification-variable-entry"
                size="tiny"
                aria-label="查看可用变量"
                :loading="showVariablesLoading"
                :aria-busy="variablesLoading"
                :disabled="variablesLoading && !showVariablesLoading"
                @click="emit('show-variables')"
              >
                <template #icon><AppIcon name="info" /></template>
              </NButton>
            </AppTooltip>
          </div>
          <NFormItem
            label="通知渠道"
            required
            :label-props="{ id: (`${formId}-field-14`) + '-label' }"
            :show-feedback="Boolean(fieldError('notificationChannel') || resourceState?.loading || resourceState?.error)"
            :validation-status="fieldError('notificationChannel') || (!resourceState?.loading && resourceState?.error) ? 'error' : undefined"
            class="form-field"
            data-task-field="notificationChannel"
          >
            <AppSelect
              :labelled-by="(`${formId}-field-14`) + '-label'"
              :id="`${formId}-field-14`"
              v-model="action.notificationChannel"
              :before-open="loadResource"
              :disabled="resourceState?.loading"
              :invalid="invalidField === 'notificationChannel'"
              :options="[
                ...channelOptions,
                ...(action.notificationChannel &&
                !channelOptions.some((option) => option.value === action.notificationChannel)
                  ? [{ value: action.notificationChannel, label: action.notificationChannel }]
                  : []),
              ]"
            />


            <template #feedback>
              <span :role="!fieldError('notificationChannel') && resourceState?.loading ? 'status' : 'alert'">{{
            fieldError('notificationChannel') || (resourceState?.loading ? '正在加载通知渠道…' : resourceState?.error + '，可展开选择器重试')
          }}</span>
            </template>
          </NFormItem>
        </template>
        <NFormItem
          v-else-if="action.type === 'Keyboard'"
          :show-label="false"
          :show-feedback="Boolean(fieldError('keys'))"
          :validation-status="fieldError('keys') ? 'error' : undefined"
          :feedback="fieldError('keys')"
        >
          <KeyboardKeyPicker
            v-model="action.keys"
            style="width: 100%"
            data-task-field="keys"
            :invalid="invalidField === 'keys'"
            collapsible
          />
        </NFormItem>
      </div>
    </TaskConfigTransition>
    <NCollapseTransition :show="Boolean(unassignedError)">
      <div class="task-validation-feedback">
        <NAlert type="error" role="alert">{{ unassignedError }}</NAlert>
      </div>
    </NCollapseTransition>
  </div>
</template>

<style>
.notification-content-field { position: relative; }
.notification-content-field .n-form-item-label { padding-right: 34px; }
.notification-variable-entry { position: absolute; top: 0; right: 0; }
.task-action-editor { min-width: 0; }
.task-process-options { display: flex; align-items: center; gap: 24px; }
</style>
