<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { apiFetch, isAbortError, userErrorMessage } from '../../api/client';
import PluginPageHost from './page/PluginPageHost.vue';

type PluginFeature = { id: string; name: string; description?: string; mode: 'page'; entry: string };
const route = useRoute();
const router = useRouter();
const pluginId = computed(() => String(route.params.pluginId ?? ''));
const featureId = computed(() => String(route.params.featureId ?? ''));
const feature = ref<PluginFeature>();
const pluginName = ref('');
const pageSessionId = ref('');
const webVersion = ref('');
const error = ref('');
const pageReady = ref(false);
let heartbeatTimer: number | undefined;
let heartbeatController: AbortController | undefined;
let loadController: AbortController | undefined;
let loadGeneration = 0;
let disposed = false;

function stopHeartbeat() {
  window.clearInterval(heartbeatTimer);
  heartbeatTimer = undefined;
  heartbeatController?.abort();
  heartbeatController = undefined;
}

function closeSession(id: string) {
  if (!id) return;
  void fetch(`/api/plugin-page-sessions/${encodeURIComponent(id)}`, {
    method: 'DELETE', keepalive: true,
  }).catch(() => undefined);
}

function checkSession(id: string) {
  if (disposed || !id || pageSessionId.value !== id || heartbeatController) return;
  const controller = new AbortController();
  heartbeatController = controller;
  const timeout = window.setTimeout(() => controller.abort(), 10_000);
  void fetch(`/api/plugin-page-sessions/${encodeURIComponent(id)}/heartbeat`, { method: 'POST', signal: controller.signal })
    .then(response => { if (response.status === 404 && !controller.signal.aborted) handleSessionExpired(id); })
    .catch(() => undefined)
    .finally(() => {
      window.clearTimeout(timeout);
      if (heartbeatController === controller) heartbeatController = undefined;
    });
}

function startHeartbeat(id: string) {
  stopHeartbeat();
  heartbeatTimer = window.setInterval(() => checkSession(id), 30_000);
}

function handleVisibilityChange() {
  if (document.visibilityState === 'visible' && pageSessionId.value) {
    startHeartbeat(pageSessionId.value);
    checkSession(pageSessionId.value);
  }
}

async function load() {
  const generation = ++loadGeneration;
  loadController?.abort();
  const controller = new AbortController();
  loadController = controller;
  stopHeartbeat();
  if (pageSessionId.value) {
    closeSession(pageSessionId.value);
    pageSessionId.value = '';
    webVersion.value = '';
  }
  feature.value = undefined;
  pageReady.value = false;
  error.value = '';
  try {
    const sessionResponse = await apiFetch(
      `/api/plugins/${encodeURIComponent(pluginId.value)}/features/${encodeURIComponent(featureId.value)}/page-sessions`,
      { method: 'POST', signal: controller.signal },
    );
    if (!sessionResponse.ok) throw new Error('无法创建页面会话');
    const session = (await sessionResponse.json()) as {
      id: string;
      pluginName: string;
      feature: PluginFeature;
      webVersion: string;
    };
    const sessionId = session.id;
    if (disposed || generation !== loadGeneration) {
      closeSession(sessionId);
      return;
    }
    if (!sessionId || session.feature?.mode !== 'page' || typeof session.feature.entry !== 'string') {
      closeSession(sessionId);
      throw new Error('页面功能不存在');
    }
    pluginName.value = session.pluginName || pluginId.value;
    feature.value = session.feature;
    webVersion.value = session.webVersion;
    pageSessionId.value = sessionId;
    startHeartbeat(sessionId);
  } catch (cause) {
    if (!disposed && generation === loadGeneration && !isAbortError(cause))
      error.value = userErrorMessage(cause, '无法打开插件功能页') || '无法打开插件功能页';
  }
}

function goBack() {
  if (sessionStorage.getItem('winpilot-plugin-page-source') === 'home') {
    void router.push('/home');
    return;
  }
  if (window.history.length > 1) router.back();
  else void router.push(`/plugins/${encodeURIComponent(pluginId.value)}`);
}

function handleSessionExpired(id: string) {
  if (disposed || pageSessionId.value !== id) return;
  pageSessionId.value = '';
  void load();
}

function handlePageReady(id: string) {
  if (!disposed && pageSessionId.value === id) pageReady.value = true;
}

function handlePageFailed(id: string, message: string) {
  if (disposed || pageSessionId.value !== id) return;
  stopHeartbeat();
  closeSession(id);
  pageSessionId.value = '';
  pageReady.value = false;
  error.value = message || '无法加载插件页面';
}


const pageTitle = computed(() =>
  feature.value && pluginName.value ? `${pluginName.value} - ${feature.value.name}` : '',
);
onMounted(() => {
  document.addEventListener('visibilitychange', handleVisibilityChange);
  void load();
});
watch([pluginId, featureId], load);
onBeforeUnmount(() => {
  const pageSource = sessionStorage.getItem('winpilot-plugin-page-source');
  const destination = router.currentRoute.value.path;
  if (pageSource === 'home' && destination !== '/home')
    sessionStorage.removeItem('winpilot-home-scroll-y');
  if (pageSource === 'plugin-detail') {
    const savedScroll = sessionStorage.getItem('winpilot-plugin-detail-scroll');
    sessionStorage.removeItem('winpilot-plugin-detail-scroll');
    if (destination === `/plugins/${encodeURIComponent(pluginId.value)}` && savedScroll !== null)
      sessionStorage.setItem('winpilot-plugin-detail-restore', savedScroll);
  }
  if (pageSource) sessionStorage.removeItem('winpilot-plugin-page-source');
  disposed = true;
  document.removeEventListener('visibilitychange', handleVisibilityChange);
  loadGeneration++;
  loadController?.abort();
  stopHeartbeat();
  if (pageSessionId.value) closeSession(pageSessionId.value);
});

</script>

<template>
  <section class="plugin-feature-page-view">
    <header class="plugin-module plugin-feature-page-header">
      <div>
        <h2>{{ pageTitle }}</h2>
        <p v-if="feature?.description" class="plugin-feature-page-description">{{ feature.description }}</p>
      </div>
      <button class="button button-secondary" type="button" @click="goBack">返回</button>
    </header>
    <div v-if="error" class="status-message error">
      <span>{{ error }}</span>
    </div>
    <PluginPageHost
      v-else-if="feature && pageSessionId"
      :key="pageSessionId"
      :plugin-id="pluginId"
      :entry="feature.entry"
      :version="webVersion"
      :session-id="pageSessionId"
      @session-expired="handleSessionExpired(pageSessionId)"
      @failed="handlePageFailed"
      @ready="handlePageReady"
    />
    <p v-if="!error && !pageReady" class="plugin-empty loading-hint" role="status">正在加载功能页…</p>
  </section>
</template>

<style scoped>
.plugin-feature-page-view { min-width: 0; }
.plugin-feature-page-header { display: flex; align-items: center; justify-content: space-between; gap: 16px; margin-bottom: 24px; }
.plugin-feature-page-header h2 { margin: 0; }
.plugin-feature-page-description { margin: 6px 0 0; color: var(--text-muted); font-size: 14px; }
@media (max-width: 640px) {
  .plugin-feature-page-header { align-items: stretch; flex-direction: column; }
}
</style>
