<script setup lang="ts" generic="Value extends string | number = string">
import { computed } from 'vue';
import { NSelect } from 'naive-ui';
import { useSelectOpening } from '../ui/useSelectOpening';
import { selectFallbackOption, withSelectLabels } from '../ui/selectLabel';

export type AppSelectOption<Value extends string | number = string | number> = { value: Value; label: string; disabled?: boolean };
const props = withDefaults(defineProps<{
  modelValue: Value | null | undefined;
  options: readonly AppSelectOption<Value>[];
  disabled?: boolean;
  clearable?: boolean;
  invalid?: boolean;
  placeholder?: string;
  ariaLabel?: string;
  labelledBy?: string;
  id?: string;
  beforeOpen?: () => Promise<boolean | void>;
}>(), { disabled: false, clearable: false, placeholder: '请选择' });
const emit = defineEmits<{ 'update:modelValue': [value: Value | '']; change: [value: Value | ''] }>();
const selectedValue = computed(() => {
  const option = props.options.find(option => String(option.value) === String(props.modelValue ?? ''));
  return option ? option.value : props.modelValue === '' ? null : props.modelValue ?? null;
});
const { open, loading, updateShow } = useSelectOpening(() => props.disabled, () => props.beforeOpen);
function choose(value: string | number | null) {
  const selected = (value ?? '') as Value | '';
  emit('update:modelValue', selected);
  emit('change', selected);
}
</script>

<template>
  <NSelect
    class="app-select"
    :fallback-option="selectFallbackOption"
    :aria-labelledby="labelledBy"
    :value="selectedValue"
    :options="withSelectLabels(options)"
    :disabled="disabled"
    :clearable="clearable"
    :loading="loading"
    :show="open"
    :placeholder="placeholder"
    :status="invalid ? 'error' : undefined"
    :input-props="{ id, 'aria-label': ariaLabel, 'aria-labelledby': labelledBy, 'aria-invalid': invalid || undefined }"
    @update:show="updateShow"
    @update:value="choose"
  />
</template>
