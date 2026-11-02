<script setup lang="ts">
import { watch } from 'vue';
import { useLoadingBar } from 'naive-ui';
import { navigationStatus } from '../ui/navigationProgress';
import { usePanelEffects } from '../ui/panelEffects';

const loadingBar = useLoadingBar();
const { loadingBarEnabled } = usePanelEffects();
watch([navigationStatus, loadingBarEnabled], ([status, enabled]) => {
  if (!enabled) loadingBar.finish();
  else if (status === 'loading') loadingBar.start();
  else if (status === 'error') loadingBar.error();
  else loadingBar.finish();
}, { immediate: true, flush: 'sync' });
</script>

<template></template>
