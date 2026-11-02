<script setup lang="ts">
import { NSpin } from 'naive-ui';

withDefaults(defineProps<{
  label?: string;
}>(), {
  label: '正在加载',
});
</script>

<template>
  <div class="app-loading app-loading--loading" role="status" :aria-label="label" aria-busy="true">
    <NSpin size="small"><template #description>{{ label }}</template></NSpin>
  </div>
</template>

<style scoped>
.app-loading { display: grid; min-width: 0; gap: 12px; margin-block: 12px; }
.app-loading--loading { min-height: 96px; place-items: center; }
</style>
