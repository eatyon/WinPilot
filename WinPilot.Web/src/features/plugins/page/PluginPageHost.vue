<script setup lang="ts">
import { onBeforeUnmount, onMounted, shallowRef } from 'vue';
import { apiFetch, userErrorMessage } from '../../../api/client';
import { getPageScroller } from '../../../ui/scrollLock';
import PluginPageRenderer from './PluginPageRenderer.vue';
import type { PluginPageHost, PluginPageModule, PluginView } from './pluginPageTypes';
import { validatePluginView } from './pluginPageRuntime';
import { combinePageSignal, createPluginPageLifetime } from './pluginPageLifetime';
import { createPluginPageScroll } from './pluginPageScroll';
import { PluginPageSessionExpiredError, readPluginResponse } from './pluginPageResponse';

const props = defineProps<{ pluginId: string; entry: string; version: string; sessionId: string }>();
const emit = defineEmits<{ sessionExpired: []; failed: [sessionId: string, message: string]; ready: [sessionId: string] }>();
const view = shallowRef<PluginView>();
const lifetime = createPluginPageLifetime(cause => console.error('插件页面清理失败', cause));

function handleFailure(cause: unknown) {
  if (lifetime.signal.aborted) return;
  const message = userErrorMessage(cause, '无法加载插件页面') || '无法加载插件页面';
  lifetime.dispose();
  emit('failed', props.sessionId, message);
}

onMounted(async () => {
  const entry = props.entry.split('/').map(encodeURIComponent).join('/');
  const base = `/plugin-assets/${encodeURIComponent(props.pluginId)}/v/${encodeURIComponent(props.version)}/`;
  try {
    const module = await import(base + entry) as PluginPageModule;
    if (lifetime.signal.aborted) return;
    validatePluginView(module.default);
    const definition = module.default;
    view.value = {
      styles: definition.styles,
      create(scope, root) {
        const requestPlugin = async (path: string, options: RequestInit, signal?: AbortSignal) => {
          const request = combinePageSignal(scope.signal, signal);
          try {
            const response = await apiFetch(`/api/plugin-page-sessions/${encodeURIComponent(props.sessionId)}/${path}`, {
              ...options,
              method: 'POST',
              signal: request.signal,
            });
            return await readPluginResponse(response);
          } catch (cause) {
            // 会话失效需销毁当前页面资源，由外层重新创建会话和页面。
            if (cause instanceof PluginPageSessionExpiredError && !lifetime.signal.aborted) {
              lifetime.dispose();
              emit('sessionExpired');
            }
            throw cause;
          } finally { request.dispose(); }
        };
        const host: PluginPageHost = {
          ...scope,
          sessionId: props.sessionId,
          assetUrl: path => base + path.split('/').map(encodeURIComponent).join('/'),
          scroll: createPluginPageScroll(root, scope.signal, getPageScroller),
          send: (command, parameters = {}, signal) => requestPlugin('messages', {
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ command, parameters }),
          }, signal),
          upload: (command, content, parameters = {}, signal) => {
            const query = new URLSearchParams({ command, parameters: JSON.stringify(parameters) });
            return requestPlugin(`uploads?${query}`, {
              headers: { 'Content-Type': 'application/octet-stream' },
              body: content,
            }, signal);
          },
        };
        return definition.create(host, root);
      },
    };
  } catch (cause) {
    handleFailure(cause);
  }
});
onBeforeUnmount(lifetime.dispose);
</script>

<template>
  <PluginPageRenderer v-if="view" :view="view" :signal="lifetime.signal" @ready="emit('ready', props.sessionId)" @failed="handleFailure" />
</template>
