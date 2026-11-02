<script setup lang="ts">
import { computed, ref } from 'vue';
import { NButton, NCollapseTransition } from 'naive-ui';
import AppIcon from './AppIcon.vue';
import { functionKeys, mainKeyboardRows, navigationRows, numpadRows, wideKeys, keyLabel, orderKeys } from '../ui/keyboardLayout';
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
        <NButton
          v-for="key in selected"
          :key="key"
          type="primary"
          secondary
          class="selected-key"
          :disabled="disabled"
          :aria-label="`移除按键 ${keyLabel(key)}`"
          @click="toggle(key)"
        >
          {{ keyLabel(key) }} <AppIcon name="close" />
        </NButton>
      </div>
      <NButton
        v-if="collapsible"
        quaternary
        class="keyboard-expand-toggle"
        :disabled="disabled"
        :aria-expanded="expanded"
        :aria-label="expanded ? '收起键盘' : '展开键盘'"
        @click="toggleExpanded"
      >
        <AppIcon :name="expanded ? 'chevron-up' : 'chevron-down'" />
      </NButton>
    </div>
    <NCollapseTransition :show="!collapsible || expanded" :inert="collapsible && !expanded || undefined">
      <div class="physical-keyboard">
        <section class="main-keyboard">
          <div class="key-row function-row">
            <NButton
              v-for="key in functionKeys"
              :key="key"
              attr-type="button"
              class="virtual-key"
              :type="selected.includes(key) ? 'primary' : 'default'"
              :secondary="selected.includes(key)"
              :disabled="isKeyDisabled(key)"
              :aria-pressed="selected.includes(key)"
              @click="toggle(key)"
            >
              {{ keyLabel(key) }}
            </NButton>
          </div>
          <div v-for="(row, index) in mainKeyboardRows" :key="index" class="key-row">
            <NButton
              v-for="key in row"
              :key="key"
              attr-type="button"
              class="virtual-key"
              :class="{ wide: wideKeys.includes(key) }"
              :type="selected.includes(key) ? 'primary' : 'default'"
              :secondary="selected.includes(key)"
              :disabled="isKeyDisabled(key)"
              :aria-pressed="selected.includes(key)"
              @click="toggle(key)"
            >
              {{ keyLabel(key) }}
            </NButton>
          </div>
        </section>
        <div class="keyboard-extra-groups">
          <section class="keyboard-extra-group">
            <h3>导航键</h3>
            <div v-for="(row, index) in navigationRows" :key="index" class="key-row">
              <NButton
                v-for="key in row"
                :key="key"
                attr-type="button"
                class="virtual-key"
                :type="selected.includes(key) ? 'primary' : 'default'"
                :secondary="selected.includes(key)"
                :disabled="isKeyDisabled(key)"
                :aria-pressed="selected.includes(key)"
                @click="toggle(key)"
              >
                {{ keyLabel(key) }}
              </NButton>
            </div>
          </section>
          <section class="keyboard-extra-group">
            <h3>数字键盘</h3>
            <div v-for="(row, index) in numpadRows" :key="index" class="key-row">
              <NButton
                v-for="key in row"
                :key="key"
                attr-type="button"
                class="virtual-key"
                :class="{ wide: wideKeys.includes(key) }"
                :type="selected.includes(key) ? 'primary' : 'default'"
                :secondary="selected.includes(key)"
                :disabled="isKeyDisabled(key)"
                :aria-pressed="selected.includes(key)"
                @click="toggle(key)"
              >
                {{ keyLabel(key) }}
              </NButton>
            </div>
          </section>
        </div>
      </div>
    </NCollapseTransition>
  </div>
</template>

<style>
.keyboard-help, .keyboard-extra-group h3 { transition: color .3s cubic-bezier(.4, 0, .2, 1); }
.keyboard-help { margin: 0; color: var(--text-muted); font-size: 13px; line-height: 1.55; }
.virtual-keyboard { display: grid; gap: 0; }
.virtual-keyboard > .selected-keys { margin-top: 10px; }
.selected-keys, .main-keyboard, .function-row, .keyboard-extra-group { transition: border-color .3s cubic-bezier(.4, 0, .2, 1), color .3s cubic-bezier(.4, 0, .2, 1); }
.selected-keys { display: flex; min-height: var(--control-height); align-items: center; gap: 7px; padding: 7px 9px; border: 1px solid var(--border); border-radius: var(--radius-sm); color: var(--text-subtle); font-size: var(--control-font-size); }
.selected-key-values { display: flex; min-width: 0; min-height: var(--control-height); flex: 1; flex-wrap: wrap; align-items: center; gap: 7px; }
.selected-keys.is-collapsible { position: relative; padding-right: 46px; }
.selected-keys.is-collapsible .selected-key-values { position: relative; z-index: 1; pointer-events: none; }
.selected-keys.is-collapsible .selected-key { pointer-events: auto; }
.keyboard-expand-toggle.n-button { position: absolute; inset: 0; width: 100%; height: 100%; padding: 0 19px; justify-content: flex-end; }
.key-row { display: flex; flex-wrap: wrap; gap: 6px; }
.virtual-key { min-width: 38px; padding: 0 8px; }
.virtual-key.wide { min-width: 78px; }
.physical-keyboard { display: grid; gap: 12px; padding-top: 10px; }
.main-keyboard { display: grid; align-content: start; gap: 6px; padding: 10px; border: 1px solid var(--border); border-radius: var(--radius-sm); }
.function-row { padding-bottom: 6px; border-bottom: 1px solid var(--border); }
.keyboard-extra-groups { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); align-content: start; gap: 12px; }
.keyboard-extra-group { display: grid; align-content: start; gap: 6px; padding: 10px; border: 1px solid var(--border); border-radius: var(--radius-sm); }
.keyboard-extra-group h3 { margin: 0 0 2px; color: var(--text-muted); font-size: 14px; font-weight: 500; }
.selected-keys[aria-invalid="true"] { border-color: var(--danger-border); }
@media (max-width: 767px) {
  .virtual-key { min-width: 35px; }
  .virtual-key.wide { min-width: 68px; }
  .physical-keyboard { grid-template-columns: 1fr; }
  .keyboard-extra-groups { grid-template-columns: 1fr; }
}
.selected-key.n-button .n-button__content { gap: 6px; }
.selected-key .app-icon { width: 14px; height: 14px; }
</style>
