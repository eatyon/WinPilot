<script setup lang="ts">
import { onBeforeUnmount, onMounted, shallowRef } from 'vue';
import { apiFetch, isAbortError, userErrorMessage } from '../../../api/client';
import { getPageScroller } from '../../../ui/scrollLock';
import { showToast } from '../../../ui/toast';
import PluginPageModal from './PluginPageModal.vue';
import PluginPageRenderer from './PluginPageRenderer.vue';
import type { PluginPageHost, PluginPageModule, PluginView } from './PluginPageTypes';
import { validatePluginView } from './PluginPageMount';
import { createPluginModalStack, type PluginModalEntry } from './PluginPageModalStack';
import { combinePageSignal, createPluginPageLifetime } from './PluginPageLifetime';
import { createPluginPageScroll } from './PluginPageScroll';
import { PluginPageSessionExpiredError, readPluginResponse } from './PluginPageResponse';

const props = defineProps<{ pluginId: string; entry: string; version: string; sessionId: string }>();
const emit = defineEmits<{ sessionExpired: []; failed: [sessionId: string, message: string] }>();
const view = shallowRef<PluginView>();
const modals = shallowRef<PluginModalEntry[]>([]);
const lifetime = createPluginPageLifetime(cause => console.error('插件页面清理失败', cause));
const reportError = (cause: unknown) => {
  if (!lifetime.signal.aborted && !isAbortError(cause)) showToast(userErrorMessage(cause, '插件操作失败'), 'error');
};
const modalStack = createPluginModalStack(
  () => { modals.value = [...modalStack.entries]; },
  reportError,
);
lifetime.onDispose(modalStack.dispose);

function handleDocumentKeydown(event: KeyboardEvent) {
  const top = modalStack.entries.at(-1);
  if (event.key !== 'Escape' || !top || event.defaultPrevented) return;
  event.preventDefault();
  modalStack.dismiss(top);
}

function handleFailure(cause: unknown) {
  if (lifetime.signal.aborted) return;
  const message = userErrorMessage(cause, '无法加载插件页面') || '无法加载插件页面';
  lifetime.dispose();
  emit('failed', props.sessionId, message);
}

onMounted(async () => {
  document.addEventListener('keydown', handleDocumentKeydown, { signal: lifetime.signal });
  const entry = props.entry.split('/').map(encodeURIComponent).join('/');
  const base = `/plugin-assets/${encodeURIComponent(props.pluginId)}/v/${encodeURIComponent(props.version)}/`;
  try {
    const module = await import(base + entry) as PluginPageModule;
    if (lifetime.signal.aborted) return;
    validatePluginView(module.default);
    const definition = module.default;
    view.value = {
      styles: definition.styles,
      mount(root, scope) {
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
          confirm: modalStack.confirm,
          openModal: options => modalStack.openModal({ ...options, styles: options.styles ?? definition.styles }),
          scroll: createPluginPageScroll(root, scope.signal, getPageScroller),
          notify: (message, type = 'success') => {
            if (!scope.signal.aborted) showToast(message, type);
          },
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
        return definition.mount(root, host);
      },
    };
  } catch (cause) {
    handleFailure(cause);
  }
});
onBeforeUnmount(lifetime.dispose);
</script>

<template>
  <PluginPageRenderer v-if="view" :view="view" :signal="lifetime.signal" @failed="handleFailure" />
  <p v-else class="plugin-empty">正在加载插件模块…</p>
  <PluginPageModal
    v-for="(modal, index) in modals"
    :key="modal.id"
    :modal="{ ...modal }"
    :active="index === modals.length - 1"
    :depth="index"
    @dismiss="modalStack.dismiss(modal)"
    @accept="modalStack.close(modal, true)"
    @action="actionIndex => modalStack.runAction(modal, actionIndex)"
    @failed="cause => { reportError(cause); modalStack.close(modal); }"
  />
</template>
