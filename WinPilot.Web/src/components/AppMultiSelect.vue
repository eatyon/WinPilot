<script setup lang="ts">
import { nextTick, ref, watch } from 'vue';
import { useAnchoredMenu } from './useAnchoredMenu';
import './AppPicker.css';

export type AppMultiSelectOption = {
  value: string;
  label: string;
  disabled?: boolean;
  unavailable?: boolean;
};
const props = withDefaults(
  defineProps<{
    modelValue: readonly string[];
    options: readonly AppMultiSelectOption[];
    label: string;
    disabled?: boolean;
    maxHeight?: number;
    ariaLabel?: string;
    labelledBy?: string;
    id?: string;
    beforeOpen?: () => Promise<boolean | void>;
  }>(),
  { disabled: false, maxHeight: 220 },
);
const emit = defineEmits<{ toggle: [value: string, checked: boolean] }>();
const root = ref<HTMLElement>();
const loading = ref(false);
const {
  trigger,
  menu,
  open,
  positioned,
  menuStyle,
  close,
  toggle: toggleMenu,
} = useAnchoredMenu(root, props.maxHeight);

async function toggle() {
  if (props.disabled || loading.value) return;
  if (!open.value && props.beforeOpen) {
    loading.value = true;
    try {
      if ((await props.beforeOpen()) === false) return;
    } finally {
      loading.value = false;
    }
  }
  await toggleMenu();
}

function choose(option: AppMultiSelectOption) {
  if (!option.disabled) emit('toggle', option.value, !props.modelValue.includes(option.value));
}

async function onTriggerArrow(event: KeyboardEvent) {
  event.preventDefault();
  if (!open.value) await toggle();
  await nextTick();
  const selected = menu.value?.querySelector<HTMLInputElement>(
    '.app-multi-input:checked:not(:disabled)',
  );
  const first = menu.value?.querySelector<HTMLInputElement>('.app-multi-input:not(:disabled)');
  (selected ?? first)?.focus();
}

function onKeyDown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    close();
    trigger.value?.focus({ preventScroll: true });
    return;
  }
  if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
  const items = [
    ...(menu.value?.querySelectorAll<HTMLInputElement>('.app-multi-input:not(:disabled)') ?? []),
  ];
  if (!items.length) return;
  event.preventDefault();
  const index = items.indexOf(document.activeElement as HTMLInputElement);
  items[(index + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length]?.focus();
}

watch(
  () => props.disabled,
  (value) => {
    if (value) close();
  },
);
</script>

<template>
  <span ref="root" class="app-select app-multi-select">
    <button
      :id="id"
      ref="trigger"
      class="app-select-trigger"
      type="button"
      :disabled="disabled || loading"
      :aria-busy="loading"
      :aria-label="ariaLabel"
      :aria-labelledby="labelledBy"
      :aria-expanded="open"
      @click.prevent="toggle"
      @keydown.down="onTriggerArrow"
      @keydown.up="onTriggerArrow"
      @keydown.esc="close"
    >
      <span>{{ loading ? '正在加载…' : label }}</span>
      <span class="app-select-chevron" aria-hidden="true" />
    </button>
    <Teleport to="body">
      <div
        v-if="open"
        ref="menu"
        class="app-select-menu"
        role="group"
        :aria-label="ariaLabel || label"
        :aria-labelledby="labelledBy"
        :style="menuStyle"
        :class="{ 'is-positioned': positioned }"
        @keydown="onKeyDown"
      >
        <div class="app-select-options">
          <label
            v-for="(option, index) in options"
            :key="`${option.value}-${index}`"
            class="app-select-option app-multi-option"
            :class="{ 'is-unavailable': option.unavailable, 'is-disabled': option.disabled }"
          >
            <input
              class="app-multi-input"
              type="checkbox"
              :value="option.value"
              :checked="modelValue.includes(option.value)"
              :disabled="option.disabled"
              @change="choose(option)"
            />
            <span>{{ option.label }}</span>
          </label>
          <span v-if="options.length === 0" class="app-select-option">暂无可用配置</span>
        </div>
      </div>
    </Teleport>
  </span>
</template>
