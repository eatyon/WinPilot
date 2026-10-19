<script setup lang="ts">
import { computed, ref } from 'vue';
import '../../components/AppPicker.css';
import { functionKeys, mainKeyboardRows, navigationRows, numpadRows, wideKeys, keyLabel, orderKeys } from './keyboardLayout';
const props = withDefaults(defineProps<{ modelValue: string[]; disabled?: boolean; invalid?: boolean; help?: string; collapsible?: boolean }>(), {
  disabled: false,
  help: '可选择单个按键或组合最多 3 个按键',
  collapsible: false,
});
const emit = defineEmits<{ 'update:modelValue': [string[]]; limit: [] }>();
const selected = computed(() => orderKeys(props.modelValue));
const expanded = ref(false);
const isKeyDisabled = (key: string) => props.disabled || (selected.value.length >= 3 && !selected.value.includes(key));
function toggleExpanded() {
  if (props.collapsible && !props.disabled) expanded.value = !expanded.value;
}
function toggle(key: string) {
  if (props.disabled) return;
  if (!selected.value.includes(key) && selected.value.length >= 3) {
    emit('limit');
    return;
  }
  const next = selected.value.includes(key)
    ? selected.value.filter((item) => item !== key)
    : [...selected.value, key];
  emit('update:modelValue', orderKeys(next));
}
</script>
<template>
  <div class="virtual-keyboard">
    <p class="keyboard-help">{{ help }}</p>
    <div class="selected-keys" :class="{ 'is-collapsible': collapsible }" :aria-invalid="invalid || undefined">
      <div class="selected-key-values" aria-live="polite">
        <span v-if="!selected.length">尚未选择按键</span>
        <button v-for="key in selected" :key="key" type="button" class="selected-key" :disabled="disabled" @click="toggle(key)">
          {{ keyLabel(key) }} ×
        </button>
      </div>
      <button v-if="collapsible" class="keyboard-expand-toggle" type="button" :disabled="disabled"
        :aria-expanded="expanded"
        :aria-label="expanded ? '收起键盘' : '展开键盘'" @click="toggleExpanded">
        <span class="app-select-chevron" aria-hidden="true" />
      </button>
    </div>
    <div v-if="!collapsible || expanded" class="physical-keyboard">
      <section class="main-keyboard">
        <div class="key-row function-row">
          <button
            v-for="key in functionKeys"
            :key="key"
            type="button"
            class="virtual-key"
            :class="{ selected: selected.includes(key) }"
            :disabled="isKeyDisabled(key)"
            :aria-pressed="selected.includes(key)"
            @click="toggle(key)"
          >
            {{ keyLabel(key) }}
          </button>
        </div>
        <div v-for="(row, index) in mainKeyboardRows" :key="index" class="key-row">
          <button
            v-for="key in row"
            :key="key"
            type="button"
            class="virtual-key"
            :class="{ selected: selected.includes(key), wide: wideKeys.includes(key) }"
            :disabled="isKeyDisabled(key)"
            :aria-pressed="selected.includes(key)"
            @click="toggle(key)"
          >
            {{ keyLabel(key) }}
          </button>
        </div>
      </section>
      <div class="keyboard-extra-groups">
        <section class="keyboard-extra-group">
          <h3>导航键</h3>
          <div v-for="(row, index) in navigationRows" :key="index" class="key-row">
            <button
              v-for="key in row"
              :key="key"
              type="button"
              class="virtual-key"
              :class="{ selected: selected.includes(key) }"
              :disabled="isKeyDisabled(key)"
              :aria-pressed="selected.includes(key)"
              @click="toggle(key)"
            >
              {{ keyLabel(key) }}
            </button>
          </div>
        </section>
        <section class="keyboard-extra-group">
          <h3>数字键盘</h3>
          <div v-for="(row, index) in numpadRows" :key="index" class="key-row">
            <button
              v-for="key in row"
              :key="key"
              type="button"
              class="virtual-key"
              :class="{ selected: selected.includes(key), wide: wideKeys.includes(key) }"
              :disabled="isKeyDisabled(key)"
              :aria-pressed="selected.includes(key)"
              @click="toggle(key)"
            >
              {{ keyLabel(key) }}
            </button>
          </div>
        </section>
      </div>
    </div>
  </div>
</template>
