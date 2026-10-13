<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, useId } from 'vue';

const props = withDefaults(
  defineProps<{
    label: string;
    href?: string;
  }>(),
  { href: undefined },
);
const root = ref<HTMLElement>();
const button = ref<HTMLButtonElement>();
const open = ref(false);
const left = ref(0);
const tooltipId = useId();

function position() {
  const bounds = root.value?.getBoundingClientRect();
  if (!bounds) return;
  const width = Math.min(320, window.innerWidth - 56);
  left.value = Math.max(16, Math.min(bounds.left, window.innerWidth - width - 16)) - bounds.left;
}

function close() {
  open.value = false;
  button.value?.blur();
}

function toggle() {
  position();
  if (open.value) close();
  else open.value = true;
}

function outside(event: PointerEvent) {
  if (!root.value?.contains(event.target as Node)) close();
}

function keydown(event: KeyboardEvent) {
  if (event.key === 'Escape') close();
}

onMounted(() => {
  if (props.href) return;
  document.addEventListener('pointerdown', outside);
  document.addEventListener('keydown', keydown);
});
onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', outside);
  document.removeEventListener('keydown', keydown);
});
</script>

<template>
  <a
    v-if="href"
    class="help-hint"
    :href="href"
    target="_blank"
    rel="noopener noreferrer"
    :aria-label="label"
    :title="label"
  >
    ?
  </a>
  <span v-else ref="root" class="help-hint-wrap" :class="{ open }" @mouseenter="position">
    <button
      ref="button"
      class="help-hint"
      type="button"
      aria-label="查看说明"
      :aria-describedby="tooltipId"
      :aria-expanded="open"
      @focus="position"
      @click.stop="toggle"
    >
      ?
    </button>
    <span :id="tooltipId" class="help-hint-popover" role="tooltip" :style="{ left: `${left}px` }">{{ label }}</span>
  </span>
</template>

<style scoped>
.help-hint-wrap { position: relative; display: inline-flex; }
.help-hint { cursor: pointer; }
.help-hint-popover { position: absolute; z-index: 30; top: calc(100% + 7px); visibility: hidden; width: min(320px, calc(100vw - 56px)); padding: 9px 11px; border: 1px solid var(--border); border-radius: 6px; opacity: 0; background: var(--surface); box-shadow: 0 8px 20px rgb(22 33 45 / 14%); color: var(--text-muted); font-size: 12px; font-weight: 400; line-height: 1.55; white-space: normal; pointer-events: none; transition: opacity 120ms ease, visibility 120ms ease; }
.help-hint-wrap:focus-within .help-hint-popover, .help-hint-wrap.open .help-hint-popover { visibility: visible; opacity: 1; }
@media (hover: hover) {
  .help-hint-wrap:hover .help-hint-popover { visibility: visible; opacity: 1; }
}
</style>
