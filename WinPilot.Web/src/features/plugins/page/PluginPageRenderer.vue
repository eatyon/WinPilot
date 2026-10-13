<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import type { PluginView } from './PluginPageTypes';
import { mountPluginView } from './PluginPageMount';
import pickerStyles from '../../../components/AppPicker.css?inline';
import controlStyles from './PluginPageUi.css?inline';

const props = defineProps<{ view: PluginView; signal: AbortSignal }>();
const emit = defineEmits<{ failed: [error: unknown]; ready: [] }>();
const container = ref<HTMLElement>();
const loading = ref(true);
let runtime: ReturnType<typeof mountPluginView> | undefined;

onMounted(async () => {
  if (!container.value) return;
  try {
    const root = container.value.attachShadow({ mode: 'open' });
    runtime = mountPluginView(root, props.view, props.signal, cause => console.error('插件视图清理失败', cause), pickerStyles + '\n' + controlStyles);
    await runtime.ready;
    if (!props.signal.aborted) emit('ready');
  }
  catch (cause) {
    if (!props.signal.aborted) {
      emit('failed', cause);
    }
  } finally { loading.value = false; }
});
onBeforeUnmount(() => runtime?.dispose());
</script>

<template>
  <div ref="container" class="plugin-surface" :class="{ 'is-loading': loading }" />
</template>

<style scoped>
.plugin-surface { min-width: 0; }
.is-loading { visibility: hidden; }
</style>
