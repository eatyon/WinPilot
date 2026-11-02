<script setup lang="ts">
import { NButton, NCheckbox, NFormItem, NSelect, NText } from 'naive-ui';
import { useId } from 'vue';
import AppNumberInput from './AppNumberInput.vue';
import { useDelayedLoading } from '../ui/useDelayedLoading';
import { selectFallbackOption, withSelectLabels } from '../ui/selectLabel';
import type { RefreshInterval } from '../ui/refreshTiming';

const props = defineProps<{
  enabled: boolean;
  interval: RefreshInterval;
  customSeconds: number;
  valid: boolean;
  refreshing: boolean;
  capturedAt: string;
}>();
const emit = defineEmits<{
  'update:enabled': [value: boolean];
  'update:interval': [value: RefreshInterval];
  'update:customSeconds': [value: number];
  refresh: [];
}>();
const intervalOptions = [
  { value: '1', label: '1 秒' },
  { value: '3', label: '3 秒' },
  { value: '5', label: '5 秒' },
  { value: '10', label: '10 秒' },
  { value: 'custom', label: '自定义' },
];
function updateSeconds(value: string | number) {
  emit('update:customSeconds', value === '' ? Number.NaN : Number(value));
}
const controlId = useId();
const showLoading = useDelayedLoading(() => props.refreshing);
</script>

<template>
  <div class="refresh-toolbar">
    <div class="refresh-controls">
      <NCheckbox :checked="enabled" @update:checked="emit('update:enabled', $event)">
        自动刷新
      </NCheckbox>
      <NFormItem
        label="间隔"
        label-placement="left"
        :label-props="{ id: `${controlId}-interval-label` }"
        :show-feedback="false"
        class="refresh-field"
      >
        <NSelect
          :fallback-option="selectFallbackOption"
          :aria-labelledby="`${controlId}-interval-label`"
          :value="interval"
          :options="withSelectLabels(intervalOptions)"
          @update:value="emit('update:interval', $event)"
        />
      </NFormItem>
      <NFormItem
        v-if="interval === 'custom'"
        label="秒"
        label-placement="left"
        :label-props="{ for: `${controlId}-seconds` }"
        :show-feedback="false"
        class="refresh-field"
      >
        <AppNumberInput
          :model-value="customSeconds"
          :id="`${controlId}-seconds`"
          :min="1"
          :max="600"
          :step="1"
          inputmode="numeric"
          :aria-invalid="!valid"
          @update:model-value="updateSeconds"
        />
      </NFormItem>
      <NText v-if="interval === 'custom' && !valid" type="error" role="alert">
        请输入 1 至 600 的整数秒数
      </NText>
    </div>
    <div class="refresh-actions">
      <span class="capture-time">最近刷新：<strong>{{ capturedAt }}</strong></span>
      <NButton type="primary" :loading="showLoading" :disabled="refreshing && !showLoading" :aria-busy="refreshing" aria-label="刷新" @click="emit('refresh')">
        刷新
      </NButton>
    </div>
  </div>
</template>

<style scoped>
.refresh-toolbar, .refresh-controls, .refresh-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; }
.refresh-toolbar { justify-content: space-between; gap: 16px; }
.refresh-field { grid-template-columns: auto 112px; align-items: center; }
@media (max-width: 767px) {
  .refresh-actions { width: 100%; justify-content: space-between; }
}
</style>

<style>
.capture-time { color: var(--text-muted); font-size: 13px; white-space: nowrap; }
.capture-time, .capture-time strong { transition: color .3s cubic-bezier(.4, 0, .2, 1); }
.capture-time strong { color: var(--text); font-weight: 500; }
@media (max-width: 767px) {
  .capture-time { white-space: normal; }
}
</style>
