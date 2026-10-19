<script setup lang="ts">
import { computed } from 'vue';
import AppIcon from '../../components/AppIcon.vue';
import AppSelect from '../../components/AppSelect.vue';
import AppMultiSelect from '../../components/AppMultiSelect.vue';
import KeyboardKeyPicker from '../desktop/KeyboardKeyPicker.vue';
import { actionOptions, taskStateOptions, multiSelectionLabel, multiParameterOptions, toggleMultiOption,
  type Action, type Task, type RegisteredAction, type ActionOption } from './taskModels';
import { unavailableSelectValue } from './taskValidation';

const props = defineProps<{
  action: Action;
  tasks: readonly Pick<Task, 'name'>[];
  tags: readonly { name: string }[];
  registeredActions: readonly RegisteredAction[];
  extensionsLoaded: boolean;
  channelOptions: readonly ActionOption[];
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
  ...new Map(props.registeredActions.map(item => [item.pluginId, item])).values(),
]);
const actionDefinitions = computed(() => props.registeredActions.filter(item => item.pluginId === props.action.pluginId));
const actionDefinition = computed(() => actionDefinitions.value.find(item => item.actionId === props.action.actionId));
</script>

<template>
  <AppSelect
    data-task-field="type"
    :model-value="action.type"
    :invalid="invalidField === 'type'"
    :options="actionOptions.map(([value, label]) => ({ value, label }))"
    @change="emit('select-type', $event)"
  />
  <template v-if="action.type === 'Extension'">
    <label class="field-control" data-task-field="pluginId">
      <span>插件 *</span>
      <AppSelect
        v-model="action.pluginId"
        :disabled="!extensionsLoaded"
        :invalid="invalidField === 'pluginId'"
        :options="[
          { value: '', label: '请选择插件' },
          ...actionPlugins.map((plugin) => ({
            value: plugin.pluginId,
            label: plugin.pluginDisplayName,
          })),
          ...(action.pluginId && !actionPlugins.some((item) => item.pluginId === action.pluginId)
            ? [{ value: action.pluginId, label: `${action.pluginId}${extensionsLoaded ? '（不可用）' : ''}` }]
            : []),
        ]"
        @change="
          action.actionId = '';
          action.parameters = {};
        "
      />
    </label>
    <label class="field-control" data-task-field="actionId">
      <span>动作 *</span>
      <AppSelect
        :model-value="action.actionId || ''"
        :disabled="!extensionsLoaded"
        :invalid="invalidField === 'actionId'"
        :options="[
          { value: '', label: '请选择动作' },
          ...actionDefinitions.map((definition) => ({
            value: definition.actionId,
            label: definition.actionName,
          })),
          ...(action.actionId &&
          !actionDefinitions.some((item) => item.actionId === action.actionId)
            ? [{ value: action.actionId, label: `${action.actionId}${extensionsLoaded ? '（不可用）' : ''}` }]
            : []),
        ]"
        @change="emit('select-extension-action', $event)"
      />
    </label>
    <template v-if="action.actionId && actionDefinition">
      <div
        v-for="parameter in actionDefinition.parameters"
        :key="parameter.key"
        :data-task-field="`parameters.${parameter.key}`"
        class="field-control"
      >
        <span v-if="parameter.type !== 'Checkbox'">
          {{ parameter.label }}{{ parameter.required ? ' *' : '' }}
        </span>
        <textarea
          v-if="parameter.type === 'Textarea'"
          :aria-invalid="invalidField === `parameters.${parameter.key}` || undefined"
          v-model="action.parameters![parameter.key]"
          rows="3"
        ></textarea>
        <input
          v-else-if="parameter.type === 'Number'"
          :aria-invalid="invalidField === `parameters.${parameter.key}` || undefined"
          v-model.number="action.parameters![parameter.key]"
          type="number"
        />
        <AppSelect
          v-else-if="parameter.type === 'Select'"
          :invalid="invalidField === `parameters.${parameter.key}`"
          v-model="action.parameters![parameter.key]"
          :before-open="() => loadOptions(parameter.key)"
          :disabled="
            !actionDefinition.dynamicOptions &&
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
          <input v-model="action.parameters![parameter.key]" type="checkbox" :aria-invalid="invalidField === `parameters.${parameter.key}` || undefined" />
          <span>{{ parameter.label }}</span>
        </label>
        <AppMultiSelect
          v-else-if="parameter.type === 'MultiCheckbox'"
          :invalid="invalidField === `parameters.${parameter.key}`"
          :before-open="() => loadOptions(parameter.key)"
          :model-value="
            Array.isArray(action.parameters?.[parameter.key])
              ? action.parameters![parameter.key]
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
        <input v-else v-model="action.parameters![parameter.key]" type="text" :aria-invalid="invalidField === `parameters.${parameter.key}` || undefined" />
      </div>
    </template>
  </template>
  <template v-else-if="action.type === 'StartProgram'">
    <label class="field-control" data-task-field="programPath">
      <span>程序 / 脚本路径 *</span>
      <input v-model="action.programPath" placeholder="请输入绝对路径" :aria-invalid="invalidField === 'programPath' || undefined" />
    </label>
    <label class="field-control" data-task-field="arguments">
      <span>参数</span>
      <input v-model="action.arguments" placeholder="可留空" :aria-invalid="invalidField === 'arguments' || undefined" />
    </label>
    <label class="field-control" data-task-field="workingDirectory">
      <span>起始目录</span>
      <input v-model="action.workingDirectory" placeholder="可留空" :aria-invalid="invalidField === 'workingDirectory' || undefined" />
    </label>
    <div class="task-process-options">
      <label class="toggle-control">
        <input v-model="action.runElevated" type="checkbox" />
        <span>使用最高权限运行</span>
      </label>
      <label class="toggle-control">
        <input v-model="action.silent" type="checkbox" />
        <span>静默执行</span>
      </label>
    </div>
  </template>
  <label v-else-if="action.type === 'StartTag'" class="field-control" data-task-field="tagName">
    <span>标签 *</span>
    <AppSelect
      v-model="action.tagName"
      :before-open="loadResource"
      :disabled="resourceState?.loading"
      :invalid="invalidField === 'tagName'"
      :options="[
        { value: '', label: '请选择标签' },
        ...tags.map((tag) => ({ value: tag.name, label: tag.name })),
        ...(action.tagName && !tags.some(tag => tag.name === action.tagName)
          ? [{ value: action.tagName, label: action.tagName }] : []),
      ]"
    />
    <small v-if="resourceState?.loading" class="field-hint" role="status">正在加载快捷标签…</small>
    <small v-else-if="resourceState?.error" class="field-error" role="alert">{{ resourceState.error }}，可展开选择器重试</small>
  </label>
  <template v-else-if="action.type === 'TaskState'">
    <label class="field-control" data-task-field="taskName">
      <span>任务 *</span>
      <AppSelect v-model="action.taskName" :invalid="invalidField === 'taskName'" :options="[
        { value: '', label: '请选择任务' },
        ...tasks.map(task => ({ value: task.name, label: task.name })),
        ...(action.taskName && !tasks.some(task => task.name === action.taskName)
          ? [{ value: action.taskName, label: `${action.taskName}（不存在）` }] : []),
      ]" />
    </label>
    <label class="field-control" data-task-field="state">
      <span>状态 *</span>
      <AppSelect v-model="action.state" :invalid="invalidField === 'state'" :options="taskStateOptions" />
    </label>
  </template>
  <template v-else-if="action.type === 'CloseProcess'">
    <label class="field-control" data-task-field="processName">
      <span>进程名 *</span>
      <input v-model="action.processName" placeholder="请输入进程名" :aria-invalid="invalidField === 'processName' || undefined" />
    </label>
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
  <label v-else-if="action.type === 'Delay'" class="field-control" data-task-field="delaySeconds">
    <span>秒数 *</span>
    <input
      v-model.number="action.delaySeconds"
      :aria-invalid="invalidField === 'delaySeconds' || undefined"
      type="number"
      placeholder="请输入 1–86400 之间的整数"
      min="1"
      max="86400"
      step="1"
      title=""
      aria-label="秒数"
    />
  </label>
  <template v-else-if="action.type === 'Shell'">
    <label class="field-control" data-task-field="shell">
      <span>终端类型 *</span>
      <AppSelect
        v-model="action.shell"
        :invalid="invalidField === 'shell'"
        :options="[
          { value: 'PowerShell', label: 'PowerShell' },
          { value: 'CMD', label: 'CMD' },
        ]"
      />
    </label>
    <label class="field-control" data-task-field="command">
      <span>命令 *</span>
      <input v-model="action.command" placeholder="输入要执行的命令" :aria-invalid="invalidField === 'command' || undefined" />
    </label>
  </template>
  <template v-else-if="action.type === 'SendNotification'">
    <div class="field-control" data-task-field="notificationContent">
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
          @click="emit('show-variables')"
        >
          <AppIcon name="help" />
        </button>
      </div>
      <textarea v-model="action.notificationContent" placeholder="请输入通知内容" rows="4" :aria-invalid="invalidField === 'notificationContent' || undefined"></textarea>
    </div>
    <div class="field-control" data-task-field="notificationChannel">
      <span>
        发送渠道
        <b>*</b>
      </span>
      <AppSelect
        v-model="action.notificationChannel"
        :before-open="loadResource"
        :disabled="resourceState?.loading"
        :invalid="invalidField === 'notificationChannel'"
        :options="[
          ...channelOptions,
          ...(action.notificationChannel && !channelOptions.some(option => option.value === action.notificationChannel)
            ? [{ value: action.notificationChannel, label: action.notificationChannel }] : []),
        ]"
      />
      <small v-if="resourceState?.loading" class="field-hint" role="status">正在加载通知渠道…</small>
      <small v-else-if="resourceState?.error" class="field-error" role="alert">{{ resourceState.error }}，可展开选择器重试</small>
    </div>
  </template>
  <KeyboardKeyPicker v-else-if="action.type === 'Keyboard'" v-model="action.keys" data-task-field="keys" :invalid="invalidField === 'keys'" collapsible />
  <small v-if="error" class="field-error">
    {{ error }}
  </small>
</template>
