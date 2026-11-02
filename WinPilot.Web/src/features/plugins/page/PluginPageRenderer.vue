<script setup lang="ts">
import { nextTick, onBeforeUnmount, onErrorCaptured, onMounted, ref, shallowRef, type Component } from 'vue';
import type { PluginView } from './pluginPageTypes';
import { preparePluginView } from './pluginPageRuntime';

const props = defineProps<{ view: PluginView; signal: AbortSignal }>();
const emit = defineEmits<{ failed: [error: unknown]; ready: [] }>();
const container = ref<HTMLElement>();
const loading = ref(true);
const component = shallowRef<Component>();
let runtime: ReturnType<typeof preparePluginView> | undefined;

function fail(cause: unknown) {
  runtime?.dispose();
  component.value = undefined;
  if (!props.signal.aborted) emit('failed', cause);
}
onErrorCaptured(cause => { fail(cause); return false; });

onMounted(async () => {
  if (!container.value) return;
  try {
    runtime = preparePluginView(container.value, props.view, props.signal, cause => console.error('插件视图清理失败', cause));
    runtime.signal.addEventListener('abort', () => { component.value = undefined; }, { once: true });
    const prepared = await runtime.ready;
    if (runtime.signal.aborted) return;
    component.value = prepared;
    await nextTick();
    if (!runtime.signal.aborted) emit('ready');
  }
  catch (cause) {
    fail(cause);
  } finally { loading.value = false; }
});
onBeforeUnmount(() => runtime?.dispose());
</script>

<template>
  <div ref="container" class="plugin-surface" :class="{ 'is-loading': loading }">
    <component :is="component" v-if="component" />
  </div>
</template>

<style scoped>
.plugin-surface { min-width: 0; }
.is-loading { visibility: hidden; }
</style>
