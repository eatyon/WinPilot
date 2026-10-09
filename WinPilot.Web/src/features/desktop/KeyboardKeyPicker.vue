<script setup lang="ts">
import { computed } from 'vue';
import { functionKeys, mainKeyboardRows, navigationRows, numpadRows, wideKeys, keyLabel } from './keyboardLayout';
const props = withDefaults(defineProps<{ modelValue: string[]; disabled?: boolean; help?: string }>(), {
  disabled: false,
  help: '可选择普通按键或最多 3 个组合键',
});
const emit = defineEmits<{ 'update:modelValue': [string[]]; limit: [] }>();
const selected = computed(() => props.modelValue);
function toggle(key: string) {
  if (props.disabled) return;
  if (!selected.value.includes(key) && selected.value.length >= 3) {
    emit('limit');
    return;
  }
  const next = selected.value.includes(key)
    ? selected.value.filter((item) => item !== key)
    : [...selected.value, key];
  emit('update:modelValue', next);
}
</script>
<template>
  <div class="virtual-keyboard">
    <p class="keyboard-help">{{ help }}</p>
    <div class="selected-keys" aria-live="polite">
      <span v-if="!selected.length">尚未选择按键</span>
      <button v-for="key in selected" :key="key" type="button" class="selected-key" :disabled="disabled" @click="toggle(key)">
        {{ keyLabel(key) }} ×
      </button>
    </div>
    <div class="physical-keyboard">
      <section class="main-keyboard">
        <div class="key-row function-row">
          <button
            v-for="key in functionKeys"
            :key="key"
            type="button"
            class="virtual-key"
            :class="{ selected: selected.includes(key) }"
            :disabled="disabled"
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
            :disabled="disabled"
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
              :disabled="disabled"
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
              :disabled="disabled"
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
