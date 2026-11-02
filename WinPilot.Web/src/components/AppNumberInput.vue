<script setup lang="ts">
import { NInputNumber } from 'naive-ui';
import { useAttrs, type InputHTMLAttributes } from 'vue';

defineOptions({ inheritAttrs: false });
const props = defineProps<{ modelValue: number | string | null | undefined }>();
const emit = defineEmits<{ 'update:modelValue': [value: number | ''] }>();
const attrs = useAttrs();
const nativeKeys = new Set(['min', 'max', 'step', 'id', 'title', 'inputmode', 'aria-invalid']);
const controlAttrs = () =>
  Object.fromEntries(Object.entries(attrs).filter(([key]) => !nativeKeys.has(key)));
const inputProps = (): InputHTMLAttributes => ({
  min: attrs.min == null ? undefined : String(attrs.min),
  max: attrs.max == null ? undefined : String(attrs.max),
  step: attrs.step == null ? undefined : String(attrs.step),
  id: attrs.id == null ? undefined : String(attrs.id),
  title: attrs.title == null ? undefined : String(attrs.title),
  inputmode: attrs.inputmode as InputHTMLAttributes['inputmode'],
  'aria-invalid': attrs['aria-invalid'] === true || attrs['aria-invalid'] === 'true' || undefined,
});
function updateValue(value: number | null) {
  emit('update:modelValue', value ?? '');
}
</script>

<template>
  <NInputNumber
    style="width: 100%"
    v-bind="controlAttrs()"
    :value="props.modelValue === '' || props.modelValue == null || !Number.isFinite(Number(props.modelValue)) ? null : Number(props.modelValue)"
    :show-button="false"
    :step="attrs.step == null || attrs.step === 'any' ? 1 : String(attrs.step)"
    :input-props="inputProps()"
    :status="inputProps()['aria-invalid'] ? 'error' : undefined"
    @update:value="updateValue"
  />
</template>
