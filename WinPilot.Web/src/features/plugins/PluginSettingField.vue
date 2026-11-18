<script setup lang="ts">
import { useModalState } from '../../ui/useModalState';
import AppPasswordInput from '../../components/AppPasswordInput.vue';
import { computed, onBeforeUnmount, ref } from 'vue';
import AppModal from '../../components/AppModal.vue';
import { NButton, NCard, NCheckbox, NCollapse, NCollapseItem, NFlex, NFormItem, NInput, NText } from 'naive-ui';
import AppSelect from '../../components/AppSelect.vue';
import AppMultiSelect from '../../components/AppMultiSelect.vue';
import AppNumberInput from '../../components/AppNumberInput.vue';
import { isAbortError, userErrorMessage } from '../../api/client';

import type { PluginDynamicConfigurationItem, PluginSettingField as SettingField } from './pluginModels';
import { createPluginRequests } from './pluginRequests';
const requests = createPluginRequests();
const props = defineProps<{ field: SettingField; modelValue: unknown; fieldId?: string }>();
const emit = defineEmits<{ 'update:modelValue': [value: unknown] }>();
const expandedNames = ref<Array<string | number>>([]);
onBeforeUnmount(requests.dispose);
const deleteIndexModal = useModalState(() => -1, value => value >= 0);
const deleteIndex = deleteIndexModal.data;
const loadedOptions = ref<NonNullable<SettingField['options']>>();
const selectError = ref('');
const fieldName = () => props.field.name;
function normalizeOptions(options: SettingField['options']) {
  return (options ?? []).map((option) => ({
    value: typeof option === 'string' ? option : option.value,
    label: typeof option === 'string' ? option : option.label,
  }));
}
const multiValues = computed<string[]>(() =>
  Array.isArray(props.modelValue) ? props.modelValue as string[] : [],
);
const multiOptions = computed(() => normalizeOptions(props.field.options));
const fixedItems = computed(() => (props.modelValue || {}) as Record<string, Record<string, unknown>>);
const dynamicItems = computed(() => (props.modelValue || []) as PluginDynamicConfigurationItem[]);
function collectionItemName(index: number): string {
  return dynamicItems.value[index]?.__winpilot?.remark?.trim() || `配置 ${index + 1}`;
}
function toggleMultiValue(value: string, checked: boolean) {
  const values = multiValues.value;
  emit(
    'update:modelValue',
    checked
      ? values.includes(value)
        ? [...values]
        : [...values, value]
      : values.filter((item) => item !== value),
  );
}
const selectOptions = computed(() =>
  normalizeOptions(loadedOptions.value ?? props.field.options),
);
async function loadSelectOptions(): Promise<boolean> {
  if (!props.field.optionsSource) return true;
  selectError.value = '';
  try {
    const options = await requests.run<unknown>(
      `/api/plugins/options/settings/${encodeURIComponent(props.field.optionsSource)}`,
      {},
      (response) => {
        if (!response.ok) throw new Error('读取通知渠道失败');
        return response.json();
      },
    );
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
    if (!isAbortError(cause))
      selectError.value = userErrorMessage(cause, '读取通知渠道失败') || '读取通知渠道失败';
    return false;
  }
}
const collectionOptions = computed(() => ({
  mode: props.field.mode,
  items: props.field.items || [],
  fields: props.field.item?.fields || [],
}));
function fieldDefault(field: SettingField): unknown {
  if (field.default !== undefined)
    return Array.isArray(field.default) ? [...field.default] : field.default;
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
  expandedNames.value.push(id);
  emit('update:modelValue', [
    ...(Array.isArray(props.modelValue) ? props.modelValue : []),
    {
      __winpilot: { id, remark: '' },
      ...Object.fromEntries(fields.map((field) => [field.name, fieldDefault(field)])),
    },
  ]);
}
function requestDelete(index: number) {
  deleteIndexModal.open(index);
}
function confirmDelete() {
  if (deleteIndex.value >= 0) {
    expandedNames.value = expandedNames.value.filter(id => id !== dynamicItems.value[deleteIndex.value]?.__winpilot?.id);
    emit(
      'update:modelValue',
      dynamicItems.value.filter((_, index) => index !== deleteIndex.value),
    );
  }
  deleteIndexModal.close();
}
</script>

<template>
  <NCard embedded size="small" content-class="plugin-setting-field">
    <template v-if="field.type === 'collection'">
      <div v-if="field.mode === 'fixed'" class="plugin-collection-fixed">
        <div class="plugin-collection-fixed-heading">
          <NText tag="h3" :depth="2">{{ field.label || fieldName() }}</NText>
          <NText v-if="field.description" tag="p" :depth="2">{{ field.description }}</NText>
        </div>
        <section
          v-for="item in collectionOptions.items"
          :key="item.name"
          class="plugin-collection-fixed-item"
        >
          <NText v-if="item.label" tag="h4" :depth="2">{{ item.label }}</NText>
          <PluginSettingField
            v-for="child in item.fields"
            :key="child.name"
            :field="child"
            v-model="fixedItems[item.name][child.name]"
            :field-id="`collection-${fieldName()}-${item.name}-${child.name}`"
          />
        </section>
      </div>
      <template v-else>
        <div class="plugin-collection-heading">
          <label class="plugin-setting-label">
            <NText :depth="2">{{ field.label || fieldName() }}</NText>
            <NText v-if="field.description" tag="small" :depth="2">{{ field.description }}</NText>
          </label>
          <NButton @click="addCollectionItem">添加</NButton>
        </div>
        <NCollapse v-model:expanded-names="expandedNames" class="plugin-collection-dynamic" display-directive="if">
          <NCollapseItem
            v-for="(item, index) in dynamicItems"
            :key="String(item.__winpilot?.id)"
            :name="String(item.__winpilot?.id)"
          >
            <template #header="{ collapsed }">
              <NButton text strong class="plugin-collection-title" :aria-expanded="!collapsed">{{ collectionItemName(index) }}</NButton>
            </template>
            <div class="plugin-collection-item-fields">
              <NCard embedded size="small" content-class="plugin-setting-field plugin-collection-remark-field">
                <NFormItem
                  label="备注"
                  :label-props="{ for: `${fieldId || fieldName()}-${item.__winpilot.id}-remark` }"
                  :show-feedback="false"
                  class="form-field"
                >
                  <NInput
                    v-model:value="item.__winpilot.remark"
                    placeholder="选填，用于区分配置"
                    :input-props="{ id: `${fieldId || fieldName()}-${item.__winpilot.id}-remark` }"
                    maxlength="100"
                  />
                </NFormItem>
              </NCard>
              <PluginSettingField
                v-for="child in collectionOptions.fields"
                :key="child.name"
                :field="child"
                v-model="item[child.name]"
                :field-id="`collection-${fieldName()}-${index}-${child.name}`"
              />
              <NFlex justify="end">
                <NButton type="error" @click="requestDelete(Number(index))">删除</NButton>
              </NFlex>
            </div>
          </NCollapseItem>
        </NCollapse>
      </template>
      <Teleport to="body">
        <AppModal
          :show="deleteIndexModal.show"
          @after-leave="deleteIndexModal.afterLeave"
          v-if="deleteIndex >= 0"
          title="确认删除配置"
          @close="deleteIndexModal.close()"
        >
          <p>确认删除配置“{{ collectionItemName(deleteIndex) }}”吗？</p>
          <template #actions>
            <NButton @click="deleteIndexModal.close()">取消</NButton>
            <NButton type="error" @click="confirmDelete">删除</NButton>
          </template>
        </AppModal>
      </Teleport>
    </template>
    <NFormItem
      v-if="field.type !== 'collection'"
      :show-label="field.type !== 'checkbox'"
      :required="field.required"
      :label-props="field.type === 'select' || field.type === 'multi-checkbox' ? { id: `${fieldId || `setting-${fieldName()}`}-label` } : { for: fieldId || `setting-${fieldName()}` }"
      :show-feedback="Boolean(selectError)"
      :validation-status="selectError ? 'error' : undefined"
      class="form-field"
    >
      <template #label>
        <NText :depth="2">{{ field.label || fieldName() }}</NText>
      </template>
      <NFlex vertical :size="4" style="width: 100%; min-width: 0">
        <NText v-if="field.type !== 'checkbox' && field.description" tag="small" :depth="2">{{ field.description }}</NText>
        <NCheckbox
          v-if="field.type === 'checkbox'"
          :checked="Boolean(modelValue)"
          :id="fieldId || `setting-${fieldName()}`"
          @update:checked="emit('update:modelValue', $event)"
        >
          <span class="plugin-setting-label">
            <NText :depth="2">{{ field.label || fieldName() }}{{ field.required ? ' *' : '' }}</NText>
            <NText v-if="field.description" tag="small" :depth="2">{{ field.description }}</NText>
          </span>
        </NCheckbox>
        <AppNumberInput
          v-else-if="field.type === 'number'"
          :model-value="modelValue as number | string | null | undefined"
          :id="fieldId || `setting-${fieldName()}`"
          :min="field.min"
          :max="field.max"
          :step="field.step ?? 'any'"
          @update:model-value="emit('update:modelValue', $event)"
        />
        <template v-else-if="field.type === 'select'">
          <AppSelect
            :id="fieldId || `setting-${fieldName()}`"
            :labelled-by="`${fieldId || `setting-${fieldName()}`}-label`"
            :model-value="modelValue as string | number | null | undefined"
            :options="selectOptions"
            :clearable="field.optionsSource === 'notificationChannels'"
            :before-open="field.optionsSource ? loadSelectOptions : undefined"
            @update:model-value="emit('update:modelValue', $event)"
          />
        </template>
        <AppMultiSelect
          v-else-if="field.type === 'multi-checkbox'"
          :id="fieldId || `setting-${fieldName()}`"
          :labelled-by="`${fieldId || `setting-${fieldName()}`}-label`"
          :model-value="multiValues"
          :options="multiOptions"
          :label="multiValues.length ? `已选择 ${multiValues.length} 项` : '请选择'"
          :aria-label="field.label || fieldName()"
          @toggle="toggleMultiValue"
        />
        <NInput
          v-else-if="field.type === 'textarea'"
          type="textarea"
          :value="String(modelValue ?? '')"
          :input-props="{ id: fieldId || `setting-${fieldName()}` }"
          @update:value="emit('update:modelValue', $event)"
        />
        <AppPasswordInput
          v-else-if="field.type === 'text' && field.masked === true"
          :value="String(modelValue ?? '')"
          :input-props="{ id: fieldId || `setting-${fieldName()}`, autocomplete: 'off' }"
          @update:value="emit('update:modelValue', $event)"
        />
        <NInput
          v-else
          :value="String(modelValue ?? '')"
          :input-props="{ id: fieldId || `setting-${fieldName()}` }"
          @update:value="emit('update:modelValue', $event)"
        />
      </NFlex>
      <template #feedback><span role="alert">{{ selectError }}</span></template>
    </NFormItem>
  </NCard>
</template>

<style>
.plugin-setting-field { display: grid; gap: 10px; color: var(--text-muted); font-size: 14px; transition: color .3s cubic-bezier(.4, 0, .2, 1); }
.plugin-setting-label { display: grid; gap: 4px; }
.plugin-setting-label > span { font-size: 14px; font-weight: 400; line-height: 1.35; }
.plugin-setting-label small { font-size: 12px; font-weight: 400; line-height: 1.5; }
.plugin-collection-fixed { display: grid; gap: 14px; min-width: 0; }
.plugin-collection-fixed-heading, .plugin-collection-fixed-item + .plugin-collection-fixed-item { transition: border-color .3s cubic-bezier(.4, 0, .2, 1); }
.plugin-collection-fixed-heading { display: grid; gap: 4px; padding-bottom: 12px; border-bottom: 1px solid var(--border); }
.plugin-collection-fixed-heading h3, .plugin-collection-fixed-item h4 { margin: 0; font-size: 14px; font-weight: 500; }
.plugin-collection-fixed-heading p { margin: 0; font-size: 12px; font-weight: 400; line-height: 1.5; }
.plugin-collection-fixed-item { display: grid; gap: 10px; min-width: 0; }
.plugin-collection-fixed-item + .plugin-collection-fixed-item { margin-top: 12px; padding-top: 12px; border-top: 1px solid var(--border); }
.plugin-collection-heading { display: flex; align-items: flex-start; justify-content: space-between; gap: 14px; min-width: 0; }
.plugin-collection-heading .plugin-setting-label { min-width: 0; }
.plugin-collection-item-fields { display: grid; gap: 10px; min-width: 0; padding: 4px 0 2px; }
@media (max-width: 420px) {
  .plugin-collection-heading { align-items: stretch; flex-direction: column; }
  .plugin-collection-item-fields { gap: 8px; }
}
.plugin-setting-field > *, .plugin-setting-label { min-width: 0; overflow-wrap: anywhere; }
.plugin-collection-title.n-button { min-width: 0; flex: 1; justify-content: flex-start; white-space: normal; text-align: left; line-height: 1.5; }
</style>
