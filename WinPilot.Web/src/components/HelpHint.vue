<script setup lang="ts">
import { NButton, NPopover } from 'naive-ui';
import { onScopeDispose, ref } from 'vue';
import AppModal from './AppModal.vue';
import AppIcon from './AppIcon.vue';
import { textPopoverStyle } from '../ui/textPopoverStyle';

defineProps<{ label: string; href?: string }>();
const narrowMedia = window.matchMedia('(max-width: 767px)');
const isNarrow = ref(narrowMedia.matches);
const helpOpen = ref(false);
function updateWidth() { isNarrow.value = narrowMedia.matches; }
narrowMedia.addEventListener('change', updateWidth);
onScopeDispose(() => narrowMedia.removeEventListener('change', updateWidth));
</script>

<template>
  <NButton v-if="href" tag="a" :href="href" target="_blank" rel="noopener noreferrer" size="tiny" circle quaternary :aria-label="label">
    <template #icon><AppIcon name="info" class="help-hint-icon" /></template>
  </NButton>
  <NButton v-else-if="isNarrow" size="tiny" circle quaternary aria-label="查看说明" @click="helpOpen = true">
    <template #icon><AppIcon name="info" class="help-hint-icon" /></template>
  </NButton>
  <NPopover v-else trigger="click" :style="textPopoverStyle">
    <template #trigger>
      <NButton size="tiny" circle quaternary aria-label="查看说明">
        <template #icon><AppIcon name="info" class="help-hint-icon" /></template>
      </NButton>
    </template>
    {{ label }}
  </NPopover>
  <AppModal v-if="helpOpen" title="说明" size="small" @close="helpOpen = false">
    <p class="help-hint-content">{{ label }}</p>
  </AppModal>
</template>

<style scoped>
.n-button .help-hint-icon { width: 14px; height: 14px; opacity: 0.7; }
.help-hint-content { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
</style>
