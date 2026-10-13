<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue';
import AppModal from '../../components/AppModal.vue';
import AppIcon from '../../components/AppIcon.vue';
import AppSelect from '../../components/AppSelect.vue';
import AppMultiSelect from '../../components/AppMultiSelect.vue';
import { isAbortError, userErrorMessage } from '../../api/client';

import type { PluginSettingField as SettingField } from './PluginModels';
import { createPluginRequests } from './PluginRequests';
type DynamicMetadata = { id: string; remark: string };
const requests = createPluginRequests();
const props = defineProps<{ field: SettingField; modelValue: any; fieldId?: string }>();
const emit = defineEmits<{ 'update:modelValue': [any] }>();
const secretVisible = ref(false);
const collapsed = ref<Record<string, boolean>>({});
onBeforeUnmount(requests.dispose);
const deleteIndex = ref(-1);
const loadedOptions = ref<NonNullable<SettingField['options']>>();
const selectError = ref('');
const fieldName = () => props.field.name;
const optionValue = (option: NonNullable<SettingField['options']>[number]) => (typeof option === 'string' ? option : option.value);
const optionLabel = (option: NonNullable<SettingField['options']>[number]) => (typeof option === 'string' ? option : option.label);
const multiValues = computed<string[]>(() => Array.isArray(props.modelValue) ? props.modelValue : []);
const multiOptions = computed(() => (props.field.options ?? []).map(option => ({
  value: optionValue(option),
  label: optionLabel(option),
})));
function toggleMultiValue(value: string, checked: boolean) {
  const values = multiValues.value;
  emit('update:modelValue', checked
    ? values.includes(value) ? [...values] : [...values, value]
    : values.filter(item => item !== value));
}
const selectOptions = computed(() => {
  const options = (loadedOptions.value ?? props.field.options ?? []).map((option) => ({
    value: optionValue(option),
    label: optionLabel(option),
  }));
  if (props.field.optionsSource === 'notificationChannels' && !options.some((option) => option.value === ''))
    options.unshift({ value: '', label: '请选择' });
  return options;
});
async function loadSelectOptions(): Promise<boolean> {
  if (!props.field.optionsSource) return true;
  selectError.value = '';
  try {
    const response = await requests.fetch(
      `/api/plugins/options/settings/${encodeURIComponent(props.field.optionsSource)}`,
    );
    if (!response.ok) throw new Error('读取通知渠道失败');
    const options = (await requests.json(response)) as unknown;
    if (
      !Array.isArray(options) ||
      !options.every(
        (option) =>
          option &&
          typeof option === 'object' &&
          typeof option.value === 'string' &&
          typeof option.label === 'string',
      )
    )
      throw new Error('通知渠道数据无效');
    loadedOptions.value = options as NonNullable<SettingField['options']>;
    return true;
  } catch (cause) {
    if (!isAbortError(cause)) selectError.value = userErrorMessage(cause, '读取通知渠道失败') || '读取通知渠道失败';
    return false;
  }
}
const collectionOptions = computed(() => ({
  mode: props.field.mode,
  items: props.field.items || [],
  fields: props.field.item?.fields || [],
}));
function fieldDefault(field: SettingField): unknown {
  if (field.default !== undefined) return Array.isArray(field.default) ? [...field.default] : field.default;
  return field.type === 'checkbox'
    ? false
    : field.type === 'multi-checkbox'
      ? []
      : field.type === 'number'
        ? 0
        : '';
}
function createUuid(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
function addCollectionItem() {
  const fields = collectionOptions.value.fields;
  const id = createUuid();
  collapsed.value[id] = false;
  emit('update:modelValue', [
    ...(Array.isArray(props.modelValue) ? props.modelValue : []),
    {
      __winpilot: { id, remark: '' } satisfies DynamicMetadata,
      ...Object.fromEntries(fields.map((field) => [field.name, fieldDefault(field)])),
    },
  ]);
}
function collectionCollapsed(index: number) {
  return collapsed.value[props.modelValue[index]?.__winpilot?.id] !== false;
}
function toggleCollection(index: number) {
  collapsed.value[props.modelValue[index].__winpilot.id] = !collectionCollapsed(index);
}
function requestDelete(index: number) {
  deleteIndex.value = index;
}
function confirmDelete() {
  if (deleteIndex.value >= 0) {
    delete collapsed.value[props.modelValue[deleteIndex.value]?.__winpilot?.id];
    emit(
      'update:modelValue',
      props.modelValue.filter((_: unknown, index: number) => index !== deleteIndex.value),
    );
  }
  deleteIndex.value = -1;
}
</script>

<template>
  <div class="plugin-setting-field">
    <template v-if="field.type === 'collection'">
      <div v-if="field.mode === 'fixed'" class="plugin-collection-fixed">
        <div class="plugin-collection-fixed-heading">
          <h3>{{ field.label || fieldName() }}</h3>
          <p v-if="field.description">{{ field.description }}</p>
        </div>
        <section
          v-for="item in collectionOptions.items"
          :key="item.name"
          class="plugin-collection-fixed-item"
        >
          <h4 v-if="item.label">{{ item.label }}</h4>
          <PluginSettingField
            v-for="child in item.fields"
            :key="child.name"
            :field="child"
            v-model="(modelValue || {})[item.name][child.name]"
            :field-id="`collection-${fieldName()}-${item.name}-${child.name}`"
          />
        </section>
      </div>
      <template v-else>
        <div class="plugin-collection-heading">
          <label class="plugin-setting-label">
            <span>{{ field.label || fieldName() }}</span>
            <small v-if="field.description">{{ field.description }}</small>
          </label>
          <button class="button button-secondary" type="button" @click="addCollectionItem">添加</button>
        </div>
        <div class="plugin-collection-dynamic">
          <div
            v-for="(item, index) in modelValue || []"
            :key="String(item.__winpilot?.id)"
            class="plugin-collection-item"
          >
            <div class="plugin-collection-item-heading" @click="toggleCollection(Number(index))">
              <button class="plugin-collection-collapse" type="button">
                {{ collectionCollapsed(Number(index)) ? '▸' : '▾' }}
              </button>
              <strong>{{ item.__winpilot?.remark || '新增配置' }}</strong>
              <button class="button button-danger" type="button" @click.stop="requestDelete(Number(index))">
                删除
              </button>
            </div>
            <div v-if="!collectionCollapsed(Number(index))" class="plugin-collection-item-fields">
              <div class="plugin-setting-field plugin-collection-remark-field">
                <label class="plugin-setting-label"><span>备注 *</span></label>
                <input
                  v-model="item.__winpilot.remark"
                  class="text-input"
                  type="text"
                  maxlength="100"
                  placeholder="新增配置"
                />
              </div>
              <PluginSettingField
                v-for="child in collectionOptions.fields"
                :key="child.name"
                :field="child"
                v-model="item[child.name]"
                :field-id="`collection-${fieldName()}-${index}-${child.name}`"
              />
            </div>
          </div>
        </div>
      </template>
      <Teleport to="body">
        <AppModal v-if="deleteIndex >= 0" title="确认删除配置" @close="deleteIndex = -1">
          <p>确认删除配置“{{ modelValue[deleteIndex]?.__winpilot?.remark || '新增配置' }}”吗？</p>
          <template #actions>
            <button class="button button-secondary" type="button" @click="deleteIndex = -1">取消</button>
            <button class="button button-danger" type="button" @click="confirmDelete">删除</button>
          </template>
        </AppModal>
      </Teleport>
    </template>
    <template v-if="field.type !== 'collection'">
      <label class="plugin-setting-label" :for="fieldId || `setting-${fieldName()}`">
        <span>{{ field.label || fieldName() }}{{ field.required ? ' *' : '' }}</span>
        <small v-if="field.description">{{ field.description }}</small>
      </label>
      <input
        v-if="field.type === 'checkbox'"
        :id="fieldId || `setting-${fieldName()}`"
        :checked="modelValue"
        type="checkbox"
        class="plugin-checkbox"
        @change="emit('update:modelValue', ($event.target as HTMLInputElement).checked)"
      />
      <input
        v-else-if="field.type === 'number'"
        :id="fieldId || `setting-${fieldName()}`"
        :value="modelValue"
        class="text-input"
        type="number"
        :min="field.min"
        :max="field.max"
        :step="field.step ?? 'any'"
        title=""
        @input="emit('update:modelValue', ($event.target as HTMLInputElement).value === '' ? '' : Number(($event.target as HTMLInputElement).value))"
      />
      <template v-else-if="field.type === 'select'">
        <AppSelect
          :id="fieldId || `setting-${fieldName()}`"
          :model-value="modelValue"
          :options="selectOptions"
          :before-open="field.optionsSource ? loadSelectOptions : undefined"
          @update:model-value="emit('update:modelValue', $event)"
        />
        <small v-if="selectError" class="status-message error" role="alert">{{ selectError }}</small>
      </template>
      <AppMultiSelect
        v-else-if="field.type === 'multi-checkbox'"
        :id="fieldId || `setting-${fieldName()}`"
        :model-value="multiValues"
        :options="multiOptions"
        :label="multiValues.length ? `已选择 ${multiValues.length} 项` : '请选择'"
        :aria-label="field.label || fieldName()"
        @toggle="toggleMultiValue"
      />
      <textarea
        v-else-if="field.type === 'textarea'"
        :id="fieldId || `setting-${fieldName()}`"
        :value="modelValue"
        class="text-input"
        rows="5"
        @input="emit('update:modelValue', ($event.target as HTMLTextAreaElement).value)"
      ></textarea>
      <span
        v-else-if="field.type === 'text' && field.masked === true"
        class="secret-input plugin-secret-input"
      >
        <input
          :id="fieldId || `setting-${fieldName()}`"
          :value="modelValue"
          class="text-input"
          :type="secretVisible ? 'text' : 'password'"
          autocomplete="off"
          @input="emit('update:modelValue', ($event.target as HTMLInputElement).value)"
        />
        <button
          type="button"
          class="secret-toggle"
          :aria-label="
            secretVisible ? `隐藏${field.label || fieldName()}` : `显示${field.label || fieldName()}`
          "
          :aria-pressed="secretVisible"
          :title="secretVisible ? '隐藏' : '显示'"
          @click="secretVisible = !secretVisible"
        >
          <AppIcon :name="secretVisible ? 'eye-off' : 'eye'" />
        </button>
      </span>
      <input
        v-else
        :id="fieldId || `setting-${fieldName()}`"
        :value="modelValue"
        class="text-input"
        type="text"
        @input="emit('update:modelValue', ($event.target as HTMLInputElement).value)"
      />
    </template>
  </div>
</template>
