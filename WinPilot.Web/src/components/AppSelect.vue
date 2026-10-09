<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import { useAnchoredMenu } from './useAnchoredMenu';
import './AppPicker.css';

export type AppSelectOption = { value: string | number; label: string; disabled?: boolean };
const props = withDefaults(
  defineProps<{
    modelValue: any;
    options: readonly AppSelectOption[];
    disabled?: boolean;
    placeholder?: string;
    ariaLabel?: string;
    id?: string;
    beforeOpen?: () => Promise<boolean | void>;
  }>(),
  { disabled: false, placeholder: '请选择' },
);
const emit = defineEmits<{ 'update:modelValue': [value: any]; change: [value: any] }>();
const root = ref<HTMLElement>();
const loading = ref(false);
const { trigger, menu, open, positioned, menuStyle, close, toggle: toggleMenu } = useAnchoredMenu(root);
const selected = computed(() =>
  props.options.find((option) => String(option.value) === String(props.modelValue ?? '')),
);
const label = computed(() => selected.value?.label ?? props.placeholder);

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

function choose(option: AppSelectOption) {
  if (option.disabled) return;
  emit('update:modelValue', option.value);
  emit('change', option.value);
  close();
  trigger.value?.focus({ preventScroll: true });
}

async function onTriggerArrow(event: KeyboardEvent) {
  event.preventDefault();
  if (!open.value) await toggle();
  await nextTick();
  const selected = menu.value?.querySelector<HTMLButtonElement>(
    '.app-select-option[aria-selected="true"]:not(:disabled)',
  );
  const first = menu.value?.querySelector<HTMLButtonElement>('.app-select-option:not(:disabled)');
  const target = selected ?? first;
  target?.focus();
}

function onKeyDown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    close();
    trigger.value?.focus({ preventScroll: true });
    return;
  }
  if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
  if (!menu.value?.contains(document.activeElement)) return;
  const items = [
    ...(menu.value?.querySelectorAll<HTMLButtonElement>('.app-select-option:not(:disabled)') ?? []),
  ];
  if (!items.length) return;
  event.preventDefault();
  const index = items.indexOf(document.activeElement as HTMLButtonElement);
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
  <span ref="root" class="app-select">
    <button
      :id="id"
      ref="trigger"
      class="app-select-trigger"
      type="button"
      :disabled="disabled || loading"
      :aria-label="ariaLabel"
      :aria-busy="loading"
      aria-haspopup="listbox"
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
        role="listbox"
        :style="menuStyle"
        :class="{ 'is-positioned': positioned }"
        @keydown="onKeyDown"
      >
        <div class="app-select-options">
          <button
            v-for="(option, index) in options"
            :key="`${String(option.value)}-${index}`"
            class="app-select-option"
            type="button"
            role="option"
            :disabled="option.disabled"
            :aria-selected="String(option.value) === String(modelValue ?? '')"
            @click="choose(option)"
          >
            {{ option.label }}
          </button>
        </div>
      </div>
    </Teleport>
  </span>
</template>
