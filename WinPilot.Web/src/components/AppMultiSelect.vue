<script setup lang="ts">
import { NSelect } from 'naive-ui';
import { useSelectOpening } from '../ui/useSelectOpening';
import { selectFallbackOption, withSelectLabels } from '../ui/selectLabel';

export type AppMultiSelectOption = {
  value: string;
  label: string;
  disabled?: boolean;
  unavailable?: boolean;
};
const props = withDefaults(defineProps<{
  modelValue: readonly string[];
  options: readonly AppMultiSelectOption[];
  label: string;
  disabled?: boolean;
  invalid?: boolean;
  maxHeight?: number;
  ariaLabel?: string;
  labelledBy?: string;
  id?: string;
  beforeOpen?: () => Promise<boolean | void>;
}>(), { disabled: false, maxHeight: 220 });
const emit = defineEmits<{ toggle: [value: string, checked: boolean] }>();
const { open, loading, updateShow } = useSelectOpening(() => props.disabled, () => props.beforeOpen);
function choose(value: unknown) {
  if (!Array.isArray(value)) return;
  const next = new Set(value.filter((item): item is string => typeof item === 'string'));
  const current = new Set(props.modelValue);
  for (const option of props.options) {
    if (!option.disabled && current.has(option.value) !== next.has(option.value))
      emit('toggle', option.value, next.has(option.value));
  }
}
</script>

<template>
  <NSelect
    class="app-select app-multi-select"
    :fallback-option="selectFallbackOption"
    :aria-labelledby="labelledBy"
    multiple
    max-tag-count="responsive"
    :value="[...modelValue]"
    :options="withSelectLabels(options)"
    :placeholder="label"
    :disabled="disabled"
    :loading="loading"
    :show="open"
    :status="invalid ? 'error' : undefined"
    :menu-props="{ style: { maxHeight: maxHeight + 'px' } }"
    :input-props="{ id, 'aria-label': ariaLabel || label, 'aria-labelledby': labelledBy, 'aria-invalid': invalid || undefined }"
    @update:show="updateShow"
    @update:value="choose"
  />
</template>
