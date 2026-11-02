<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue';
import { useMessage } from 'naive-ui';
import type { ToastType } from '../ui/toast';

const message = useMessage();
function handleToast(event: Event) {
  const detail = (event as CustomEvent<{ message?: string; type?: ToastType }>).detail;
  if (typeof detail?.message !== 'string' || !detail.message) return;
  message.create(detail.message, { type: detail.type === 'success' ? 'success' : 'error' });
}
onMounted(() => window.addEventListener('winpilot:toast', handleToast));
onUnmounted(() => {
  window.removeEventListener('winpilot:toast', handleToast);
  message.destroyAll();
});
</script>

<template></template>
