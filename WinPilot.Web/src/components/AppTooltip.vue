<script setup lang="ts">
import { NPopover } from 'naive-ui';
import { onScopeDispose, ref } from 'vue';
import { textPopoverStyle } from '../ui/textPopoverStyle';

defineProps<{ text?: string; disabledTrigger?: boolean }>();
const hoverMedia = window.matchMedia('(hover: hover) and (pointer: fine)');
const canHover = ref(hoverMedia.matches);
const shown = ref(false);
function updateHover() {
  canHover.value = hoverMedia.matches;
  if (!canHover.value) shown.value = false;
}
function updateShow(value: boolean) {
  shown.value = canHover.value && value;
}
hoverMedia.addEventListener('change', updateHover);
onScopeDispose(() => hoverMedia.removeEventListener('change', updateHover));
</script>

<template>
  <NPopover trigger="hover" :disabled="!text || !canHover" :show="shown" @update:show="updateShow" :style="textPopoverStyle">
    <template #trigger>
      <span v-if="disabledTrigger" class="app-tooltip-trigger"><slot /></span>
      <slot v-else />
    </template>
    {{ text }}
  </NPopover>
</template>

<style>
.app-tooltip-trigger { display: inline-flex; min-width: 0; vertical-align: middle; }
</style>
