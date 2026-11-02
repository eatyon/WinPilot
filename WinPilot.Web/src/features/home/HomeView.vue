<script setup lang="ts">
import AppLoadError from '../../components/AppLoadError.vue';
import { useModalState } from '../../ui/useModalState';
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { RouterLink, useRouter } from 'vue-router';
import { apiFetch, isAbortError, userErrorMessage } from '../../api/client';
import { NEmpty, NButton, NCard, NModal, NSkeleton, NSpace } from 'naive-ui';
import RefreshToolbar from '../../components/RefreshToolbar.vue';
import { showToast } from '../../ui/toast';
import { getPageScroller } from '../../ui/scrollLock';
import type {
  PluginFeatureDefinition as PluginFeature,
  PluginFeatureDisplaySettings,
  PluginStatusDisplaySettings,
} from '../plugins/pluginModels';
import {
  formatRefreshTime,
  isCustomRefreshIntervalValid,
  refreshIntervalMilliseconds,
  type RefreshInterval,
} from '../../ui/refreshTiming';
type SystemResourceUsage = { name: string; usagePercent: number | null };
type SystemStatus = {
  cpuUsagePercent: number;
  memoryUsagePercent: number;
  gpuUsages: SystemResourceUsage[];
  diskUsages: SystemResourceUsage[];
};
type PluginSummary = {
  id: string;
  name: string;
  state: string;
  enabled: boolean;
  status: Record<string, unknown>;
  homeTitle: string;
};
type PluginFeatureSummary = {
  id: string;
  name: string;
  homeTitle: string;
  features: PluginFeature[];
};

const status = ref<SystemStatus>();
const errorMessage = ref<string>();
const pluginError = ref('');
const pluginsLoaded = ref(false);
const isRefreshing = ref(false);
const autoEnabled = ref(false);
const intervalOption = ref<RefreshInterval>('5');
const customIntervalSeconds = ref(15);
const capturedAt = ref<Date>();
const currentTime = ref(Date.now());
let refreshTimer: number | undefined;
let relativeTimeTimer: number | undefined;
let lastRefreshFailureAt: number | undefined;
let statusController: AbortController | undefined;
let pluginController: AbortController | undefined;
let featureController: AbortController | undefined;
let isPageActive = false;
const pluginSummaries = ref<PluginSummary[]>([]);
const statusProviderModal = useModalState<PluginSummary | undefined>(() => undefined);
const statusProvider = statusProviderModal.data;
const pluginFeatureSummaries = ref<PluginFeatureSummary[]>([]);
const featureProviderModal = useModalState<PluginFeatureSummary | undefined>(() => undefined);
const featureProvider = featureProviderModal.data;
const executingFeatureKey = ref('');
const confirmingFeatureModal = useModalState<{ plugin: PluginFeatureSummary; feature: PluginFeature } | undefined>(() => undefined);
const confirmingFeature = confirmingFeatureModal.data;
const pluginStateEvents = new Map<string, EventSource>();
const router = useRouter();
const restoringScroll = ref(Number(sessionStorage.getItem('winpilot-home-scroll-y')) > 0);

const isCustomIntervalValid = computed(
  () => isCustomRefreshIntervalValid(customIntervalSeconds.value),
);
const activeIntervalMilliseconds = computed(() =>
  refreshIntervalMilliseconds(autoEnabled.value, intervalOption.value, customIntervalSeconds.value),
);
const relativeCapturedAt = computed(() => formatRefreshTime(capturedAt.value, currentTime.value));
const resourceItems = (usages: SystemResourceUsage[] | undefined, fallbackName: string) =>
  (usages?.length ? usages : [{ name: fallbackName, usagePercent: null }]).map((usage) => ({
    name: usage.name,
    value: status.value
      ? usage.usagePercent == null
        ? '不可用'
        : formatPercentage(usage.usagePercent)
      : '—',
    available: usage.usagePercent != null,
  }));
const cpuMemoryItems = computed(() => [
  {
    name: 'CPU',
    value: status.value ? formatPercentage(status.value.cpuUsagePercent) : '—',
    available: Boolean(status.value),
  },
  {
    name: '内存',
    value: status.value ? formatPercentage(status.value.memoryUsagePercent) : '—',
    available: Boolean(status.value),
  },
]);
const gpuItems = computed(() => resourceItems(status.value?.gpuUsages, 'GPU 0'));
const diskItems = computed(() => resourceItems(status.value?.diskUsages, '磁盘 0'));
function displayPluginStatus(value: unknown): string {
  if (value === null || value === undefined || value === '') return '—';
  if (typeof value === 'boolean') return value ? '是' : '否';
  if (Array.isArray(value)) return value.map(displayPluginStatus).join('、') || '—';
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
}
function featureButtonLabel(feature: PluginFeature): string {
  return feature.mode === 'page' ? '进入' : '执行';
}
function closePluginStateEvents() {
  pluginStateEvents.forEach((source) => source.close());
  pluginStateEvents.clear();
}
async function refreshStatus() {
  if (!isPageActive || isRefreshing.value) return;
  clearAutomaticRefresh();
  statusController?.abort();
  const controller = new AbortController();
  statusController = controller;
  isRefreshing.value = true;
  try {
    const response = await apiFetch('/api/system/status', {
      cache: 'no-store',
      signal: controller.signal,
    });
    if (!response.ok) throw new Error('系统状态获取失败');
    const nextStatus = (await response.json()) as SystemStatus;
    if (!isPageActive || statusController !== controller) return;
    status.value = nextStatus;
    errorMessage.value = undefined;
    capturedAt.value = new Date();
    startRelativeTime();
    lastRefreshFailureAt = undefined;
  } catch (error) {
    if (isPageActive && statusController === controller && !isAbortError(error)) {
      errorMessage.value = userErrorMessage(error, '系统状态获取失败');
      lastRefreshFailureAt = Date.now();
    }
  } finally {
    if (statusController === controller) {
      statusController = undefined;
      if (isPageActive) {
        isRefreshing.value = false;
        configureAutomaticRefresh();
      }
    }
  }
}
async function refreshPlugins() {
  pluginController?.abort();
  const controller = new AbortController();
  pluginController = controller;
  try {
    if (!isPageActive) return;
    const response = await apiFetch('/api/plugins', {
      cache: 'no-store',
      signal: controller.signal,
    });
    if (!isPageActive || pluginController !== controller) return;
    if (!response.ok) throw new Error('无法读取插件内容');
    const plugins = (await response.json()) as Array<{
      id: string;
      manifest?: { name?: string; status_ui?: boolean; features_ui?: string };
      state: string;
      enabled: boolean;
    }>;
    if (!isPageActive || pluginController !== controller) return;
    const statusKeysByPlugin = new Map<string, string[]>();
    const statusSummariesPromise = Promise.all(
      plugins
        .filter((plugin) => plugin.enabled && plugin.manifest?.status_ui)
        .map(async (plugin) => {
          const encodedId = encodeURIComponent(plugin.id);
          const [result, settingsResult] = await Promise.all([
            apiFetch(`/api/plugins/${encodedId}/status`, {
              cache: 'no-store',
              signal: controller.signal,
            }),
            apiFetch(`/api/plugins/${encodedId}/settings/display/status`, {
              cache: 'no-store',
              signal: controller.signal,
            }),
          ]);
          const settings = settingsResult.ok
            ? ((await settingsResult.json()) as PluginStatusDisplaySettings)
            : { registerOnHome: true, statusKeys: [], homeTitle: '' };
          const rawStatus = result.ok ? ((await result.json()) as Record<string, unknown>) : {};
          const status = Object.fromEntries(
            Object.entries(rawStatus).filter(([key]) => settings.statusKeys.includes(key)),
          );
          if (!isPageActive || pluginController !== controller || controller.signal.aborted)
            throw new DOMException('刷新已取消', 'AbortError');
          if (!settings.registerOnHome || !settings.statusKeys.length) return undefined;
          statusKeysByPlugin.set(plugin.id, settings.statusKeys);
          return {
            id: plugin.id,
            name: plugin.manifest?.name ?? plugin.id,
            state: plugin.state,
            enabled: plugin.enabled,
            status,
            homeTitle: settings.homeTitle,
          };
        }),
    );
    const featureSummariesPromise = Promise.all(
      plugins
        .filter((plugin) => plugin.enabled && plugin.manifest?.features_ui)
        .map(async (plugin) => {
          const encodedId = encodeURIComponent(plugin.id);
          const [definitionResult, settingsResult] = await Promise.all([
            apiFetch(`/api/plugins/${encodedId}/features`, {
              cache: 'no-store',
              signal: controller.signal,
            }),
            apiFetch(`/api/plugins/${encodedId}/settings/display/features`, {
              cache: 'no-store',
              signal: controller.signal,
            }),
          ]);
          const definition = definitionResult.ok
            ? ((await definitionResult.json()) as { features?: PluginFeature[] })
            : { features: [] };
          const settings = settingsResult.ok
            ? ((await settingsResult.json()) as PluginFeatureDisplaySettings)
            : { registerOnHome: true, featureIds: [], homeTitle: '' };
          const features = (definition.features ?? []).filter((feature) =>
            settings.featureIds.includes(feature.id),
          );
          if (!isPageActive || pluginController !== controller || controller.signal.aborted)
            throw new DOMException('刷新已取消', 'AbortError');
          return settings.registerOnHome && features.length
            ? {
                id: plugin.id,
                name: plugin.manifest?.name ?? plugin.id,
                homeTitle: settings.homeTitle,
                features,
              }
            : undefined;
        }),
    );
    const [statusSummaries, featureSummaries] = await Promise.all([
      statusSummariesPromise,
      featureSummariesPromise,
    ]);
    if (!isPageActive || pluginController !== controller || controller.signal.aborted) return;
    pluginSummaries.value = statusSummaries.filter((item): item is PluginSummary => item !== undefined);
    pluginsLoaded.value = true;
    pluginError.value = '';
    pluginFeatureSummaries.value = featureSummaries.filter(
      (item): item is PluginFeatureSummary => item !== undefined,
    );
    closePluginStateEvents();
    for (const plugin of pluginSummaries.value) {
      const statusKeys = statusKeysByPlugin.get(plugin.id) ?? [];
      const source = new EventSource(`/api/plugins/${encodeURIComponent(plugin.id)}/events`);
      source.onmessage = (event) => {
        if (!isPageActive || controller.signal.aborted) return;
        try {
          const next = JSON.parse(event.data) as Record<string, unknown>;
          const filtered = Object.fromEntries(
            Object.entries(next).filter(([key]) => statusKeys.includes(key)),
          );
          pluginSummaries.value = pluginSummaries.value.map((item) =>
            item.id === plugin.id ? { ...item, status: filtered } : item,
          );
        } catch {}
      };
      pluginStateEvents.set(plugin.id, source);
    }
  } catch (error) {
    if (isPageActive && pluginController === controller && !isAbortError(error)) {
      pluginError.value = userErrorMessage(error, '无法读取插件内容');
      closePluginStateEvents();
    }
  } finally {
    if (pluginController === controller) pluginController = undefined;
  }
}

async function executeFeature(
  plugin: PluginFeatureSummary,
  feature: PluginFeature,
): Promise<boolean> {
  if (!isPageActive || feature.mode !== 'action' || executingFeatureKey.value) return false;
  const controller = new AbortController();
  featureController = controller;
  const key = `${plugin.id}:${feature.id}`;
  executingFeatureKey.value = key;
  try {
    const response = await apiFetch(
      `/api/plugins/${encodeURIComponent(plugin.id)}/features/${encodeURIComponent(feature.id)}/execute`,
      { method: 'POST', signal: controller.signal },
    );
    if (!response.ok) throw new Error('执行功能失败');
    const result = (await response.json()) as { succeeded: boolean; message: string };
    if (!isPageActive || controller.signal.aborted) return false;
    showToast(result.message, result.succeeded ? 'success' : 'error');
    return result.succeeded;
  } catch (error) {
    if (isPageActive && !isAbortError(error)) showToast(userErrorMessage(error, '执行功能失败'));
    return false;
  } finally {
    featureController = undefined;
    executingFeatureKey.value = '';
  }
}

function beginFeature(plugin: PluginFeatureSummary, feature: PluginFeature) {
  if (feature.mode === 'page') {
    sessionStorage.setItem('winpilot-home-scroll-y', String(getPageScroller().scrollTop));
    sessionStorage.setItem('winpilot-plugin-page-source', 'home');
    void router.push(
      `/plugins/${encodeURIComponent(plugin.id)}/features/${encodeURIComponent(feature.id)}`,
    );
    return;
  }
  if (feature.mode !== 'action' || executingFeatureKey.value) return;
  if (feature.confirm === false) void executeFeature(plugin, feature);
  else confirmingFeatureModal.open({ plugin, feature });
}

async function confirmFeature() {
  const current = confirmingFeature.value;
  if (!current || executingFeatureKey.value) return;
  if (await executeFeature(current.plugin, current.feature)) confirmingFeatureModal.close();
}

function clearAutomaticRefresh() {
  if (refreshTimer !== undefined) {
    window.clearTimeout(refreshTimer);
    refreshTimer = undefined;
  }
}
function configureAutomaticRefresh() {
  clearAutomaticRefresh();
  if (
    !isPageActive ||
    document.visibilityState !== 'visible' ||
    isRefreshing.value ||
    activeIntervalMilliseconds.value <= 0
  )
    return;
  const lastRefreshAt = lastRefreshFailureAt ?? capturedAt.value?.getTime();
  const delay =
    lastRefreshAt === undefined
      ? 0
      : Math.max(0, lastRefreshAt + activeIntervalMilliseconds.value - Date.now());
  refreshTimer = window.setTimeout(() => {
    refreshTimer = undefined;
    void refreshStatus();
  }, delay);
}
function updateRelativeTime() {
  currentTime.value = Date.now();
  if (capturedAt.value && currentTime.value - capturedAt.value.getTime() >= 3600000) {
    window.clearInterval(relativeTimeTimer);
    relativeTimeTimer = undefined;
  }
}
function startRelativeTime() {
  updateRelativeTime();
  if (
    relativeTimeTimer === undefined &&
    capturedAt.value &&
    document.visibilityState === 'visible' &&
    currentTime.value - capturedAt.value.getTime() < 3600000
  ) {
    relativeTimeTimer = window.setInterval(updateRelativeTime, 1000);
  }
}
function handlePageResume() {
  if (document.visibilityState === 'visible') startRelativeTime();
  else {
    window.clearInterval(relativeTimeTimer);
    relativeTimeTimer = undefined;
  }
  configureAutomaticRefresh();
}
function formatPercentage(value: number) {
  return `${formatNumber(value)}%`;
}
function formatNumber(value: number) {
  return new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 1 }).format(value);
}

watch([autoEnabled, intervalOption, customIntervalSeconds], configureAutomaticRefresh);
onMounted(() => {
  isPageActive = true;
  const savedScroll = sessionStorage.getItem('winpilot-home-scroll-y');
  configureAutomaticRefresh();
  void (async () => {
    try {
      await refreshPlugins();
      if (savedScroll === null || !isPageActive) return;
      await nextTick();
      if (!isPageActive) return;
      getPageScroller().scrollTo({ top: Number(savedScroll), behavior: 'auto' });
      sessionStorage.removeItem('winpilot-home-scroll-y');
    } finally {
      restoringScroll.value = false;
    }
  })();
  document.addEventListener('visibilitychange', handlePageResume);
  window.addEventListener('focus', handlePageResume);
});
onBeforeUnmount(() => {
  isPageActive = false;
  clearAutomaticRefresh();
  pluginController?.abort();
  pluginController = undefined;
  closePluginStateEvents();
  if (relativeTimeTimer !== undefined) window.clearInterval(relativeTimeTimer);
  relativeTimeTimer = undefined;
  document.removeEventListener('visibilitychange', handlePageResume);
  window.removeEventListener('focus', handlePageResume);
  statusController?.abort();
  statusController = undefined;
  featureController?.abort();
  status.value = undefined;
});
function retryPageLoads() {
  return Promise.all([refreshStatus(), refreshPlugins()]);
}
</script>

<template>
  <section
    class="system-status-view load-error-layout"
    aria-label="系统状态"
    :style="restoringScroll ? { visibility: 'hidden' } : undefined"
  >
    <AppLoadError :show="Boolean(errorMessage || pluginError)" :retry="retryPageLoads">{{ errorMessage || pluginError }}</AppLoadError>
    <NCard tag="section">
      <RefreshToolbar
        v-model:enabled="autoEnabled"
        v-model:interval="intervalOption"
        v-model:custom-seconds="customIntervalSeconds"
        :valid="isCustomIntervalValid"
        :refreshing="isRefreshing"
        :captured-at="relativeCapturedAt"
        @refresh="refreshStatus"
      />
      <div class="system-status-grid" :aria-busy="isRefreshing">
        <NCard tag="section" embedded size="small" class="system-status-card">
          <div v-for="item in cpuMemoryItems" :key="item.name" class="system-status-primary-item">
            <p>{{ item.name }}</p>
            <strong :class="{ unavailable: !item.available }">
              <NSkeleton v-if="isRefreshing && !status && !errorMessage" :width="80" height="1lh" />
              <template v-else>{{ item.value }}</template>
            </strong>
          </div>
        </NCard>
        <NCard tag="section" embedded size="small" class="system-status-card">
          <h2>GPU</h2>
          <dl class="system-status-resource-list">
            <div v-for="item in gpuItems" :key="item.name">
              <dt>{{ item.name }}</dt>
              <dd :class="{ unavailable: !item.available }"><NSkeleton v-if="isRefreshing && !status && !errorMessage" :width="40" height="1lh" /><template v-else>{{ item.value }}</template></dd>
            </div>
          </dl>
        </NCard>
        <NCard tag="section" embedded size="small" class="system-status-card">
          <h2>磁盘</h2>
          <dl class="system-status-resource-list">
            <div v-for="item in diskItems" :key="item.name">
              <dt>{{ item.name }}</dt>
              <dd :class="{ unavailable: !item.available }"><NSkeleton v-if="isRefreshing && !status && !errorMessage" :width="40" height="1lh" /><template v-else>{{ item.value }}</template></dd>
            </div>
          </dl>
        </NCard>
      </div>
    </NCard>
    <section
      v-if="pluginSummaries.length || pluginFeatureSummaries.length"
      class="home-plugin-modules"
      aria-label="插件内容"
    >
      <NCard v-for="plugin in pluginSummaries" :key="plugin.id" tag="article">
        <template #header>
          <NButton text class="home-plugin-title" @click="statusProviderModal.open(plugin)">
            {{ plugin.homeTitle }}
          </NButton>
        </template>
        <dl v-if="Object.keys(plugin.status).length" class="home-plugin-status-list">
          <div v-for="[key, value] in Object.entries(plugin.status)" :key="key">
            <dt>{{ key }}</dt>
            <dd>{{ displayPluginStatus(value) }}</dd>
          </div>
        </dl>
        <NEmpty :show-icon="false" v-else class="home-plugin-status-empty">暂无状态数据</NEmpty>
      </NCard>
      <NCard v-for="plugin in pluginFeatureSummaries" :key="`${plugin.id}-features`" tag="article">
        <template #header>
          <NButton text class="home-plugin-title" @click="featureProviderModal.open(plugin)">
            {{ plugin.homeTitle }}
          </NButton>
        </template>
        <div class="plugin-features-list">
          <NCard
            tag="article"
            v-for="feature in plugin.features"
            :key="feature.id"
            content-class="plugin-feature-item"
            embedded
            size="small"
          >
            <div>
              <h4>{{ feature.name }}</h4>
              <p v-if="feature.description">{{ feature.description }}</p>
            </div>
            <div class="plugin-feature-controls">
              <NButton
                :loading="executingFeatureKey === `${plugin.id}:${feature.id}`"
                :aria-label="`${featureButtonLabel(feature)}${feature.name}`"
                :disabled="feature.mode === 'action' && Boolean(executingFeatureKey)"
                @click="beginFeature(plugin, feature)"
              >
                {{
                  executingFeatureKey === `${plugin.id}:${feature.id}`
                    ? '正在执行'
                    : featureButtonLabel(feature)
                }}
              </NButton>
            </div>
          </NCard>
        </div>
      </NCard>
    </section>
    <NModal
      @after-leave="statusProviderModal.afterLeave"
      :internal-appear="true"
      v-if="statusProvider"
      :show="statusProviderModal.show"
      preset="dialog"
      class="app-confirm-dialog"
      :show-icon="false"
      title="状态来源"
      @update:show="
        (show) => {
          if (!show) statusProviderModal.close();
        }
      "
      :inert="!statusProviderModal.show || undefined"
    >
      <p>此状态栏由插件“{{ statusProvider.name }}”提供</p>
      <template #action>
        <NSpace justify="end">
          <RouterLink
            custom
            v-slot="{ href, navigate }"
            :to="`/plugins/${encodeURIComponent(statusProvider.id)}`"
          >
            <NButton
              tag="a"
              :href="href"
              @click="
                statusProviderModal.close();
                navigate($event);
              "
            >
              进入插件主页
            </NButton>
          </RouterLink>
          <RouterLink
            custom
            v-slot="{ href, navigate }"
            :to="{ path: `/plugins/${encodeURIComponent(statusProvider.id)}/info`, query: { focus: 'status' } }"
          >
            <NButton
              tag="a"
              type="primary"
              :href="href"
              @click="
                statusProviderModal.close();
                navigate($event);
              "
            >
              进入插件状态管理
            </NButton>
          </RouterLink>
        </NSpace>
      </template>
    </NModal>
    <NModal
      @after-leave="featureProviderModal.afterLeave"
      :internal-appear="true"
      v-if="featureProvider"
      :show="featureProviderModal.show"
      preset="dialog"
      class="app-confirm-dialog"
      :show-icon="false"
      title="功能来源"
      @update:show="
        (show) => {
          if (!show) featureProviderModal.close();
        }
      "
      :inert="!featureProviderModal.show || undefined"
    >
      <p>此功能栏由插件“{{ featureProvider.name }}”提供</p>
      <template #action>
        <NSpace justify="end">
          <RouterLink
            custom
            v-slot="{ href, navigate }"
            :to="`/plugins/${encodeURIComponent(featureProvider.id)}`"
          >
            <NButton
              tag="a"
              :href="href"
              @click="
                featureProviderModal.close();
                navigate($event);
              "
            >
              进入插件主页
            </NButton>
          </RouterLink>
          <RouterLink
            custom
            v-slot="{ href, navigate }"
            :to="{ path: `/plugins/${encodeURIComponent(featureProvider.id)}/info`, query: { focus: 'features' } }"
          >
            <NButton
              tag="a"
              type="primary"
              :href="href"
              @click="
                featureProviderModal.close();
                navigate($event);
              "
            >
              进入插件功能管理
            </NButton>
          </RouterLink>
        </NSpace>
      </template>
    </NModal>
    <NModal
      @after-leave="confirmingFeatureModal.afterLeave"
      :internal-appear="true"
      v-if="confirmingFeature"
      title="提示"
      :show="confirmingFeatureModal.show"
      preset="dialog"
      class="app-confirm-dialog"
      :show-icon="false"
      :mask-closable="!executingFeatureKey"
      :closable="!executingFeatureKey"
      :close-on-esc="!executingFeatureKey"
      @update:show="
        (show) => {
          if (!show && !executingFeatureKey) confirmingFeatureModal.close();
        }
      "
      :inert="!confirmingFeatureModal.show || undefined"
    >
      <p>确定要执行“{{ confirmingFeature.feature.name }}”吗？</p>
      <template #action>
        <NSpace justify="end">
          <NButton :disabled="Boolean(executingFeatureKey)" @click="confirmingFeatureModal.close()">
            取消
          </NButton>
          <NButton
            type="primary"
            :loading="Boolean(executingFeatureKey)"
            :disabled="Boolean(executingFeatureKey)"
            @click="confirmFeature"
          >
            {{ executingFeatureKey ? '正在执行' : '确认执行' }}
          </NButton>
        </NSpace>
      </template>
    </NModal>
  </section>
</template>

<style>
.system-status-card h2, .system-status-primary-item p, .system-status-primary-item strong, .system-status-resource-list dt, .system-status-resource-list dd, .home-plugin-status-list dt, .home-plugin-status-list dd { transition: color .3s cubic-bezier(.4, 0, .2, 1); }
.system-status-view { display: grid; gap: var(--page-card-row-gap) 18px; }
.system-status-grid, .system-status-primary-item + .system-status-primary-item, .home-plugin-status-list, .home-plugin-status-list > div {
  transition: border-color .3s cubic-bezier(.4, 0, .2, 1);
}
.system-status-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 14px; margin-top: 20px; padding-top: 20px; border-top: 1px solid var(--border); }
.system-status-card { min-width: 0; }
.system-status-card h2 { margin: 0; color: var(--text-muted); font-size: 14px; font-weight: 400; }
.system-status-primary-item + .system-status-primary-item { margin-top: 16px; padding-top: 16px; border-top: 1px solid var(--border); }
.system-status-primary-item p { margin: 0; color: var(--text-muted); font-size: 14px; font-weight: 400; }
.system-status-primary-item strong { display: block; margin-top: 7px; overflow: hidden; color: var(--text); font-size: 24px; font-variant-numeric: tabular-nums; letter-spacing: -0.035em; line-height: 1.15; text-overflow: ellipsis; white-space: nowrap; font-weight: 400; }
.system-status-primary-item strong.unavailable, .system-status-resource-list .unavailable { color: var(--text-muted); }
.system-status-resource-list { display: grid; gap: 10px; margin: 16px 0 0; }
.system-status-resource-list div { display: flex; justify-content: space-between; gap: 12px; font-size: 14px; }
.system-status-resource-list dt { min-width: 0; overflow: hidden; color: var(--text-muted); text-overflow: ellipsis; white-space: nowrap; }
.system-status-resource-list dd { margin: 0; color: var(--text); font-variant-numeric: tabular-nums; font-weight: 400; white-space: nowrap; }
@media (max-width: 767px) {
  .system-status-view .system-status-grid { grid-template-columns: 1fr; gap: 12px; }
}
@media (max-width: 767px) {
  .system-status-grid { grid-template-columns: 1fr; gap: 12px; margin-top: 16px; padding-top: 16px; }
}
.home-plugin-modules { display: grid; gap: var(--page-card-row-gap) 18px; }
.home-plugin-status-list { display: grid; margin: 0; border-top: 1px solid var(--border); }
.home-plugin-status-list div { display: grid; grid-template-columns: minmax(160px, .3fr) minmax(0, 1fr); gap: 18px; padding: 13px 0; border-bottom: 1px solid var(--border); }
.home-plugin-status-list dt { min-width: 0; color: var(--text-muted); font-size: 13px; overflow-wrap: anywhere; }
.home-plugin-status-list dd { min-width: 0; margin: 0; color: var(--text); font-size: 14px; line-height: 1.5; overflow-wrap: anywhere; white-space: pre-wrap; }
.home-plugin-status-empty { margin: 0; }
@media (max-width: 640px) {
  .home-plugin-status-list div { grid-template-columns: 1fr; gap: 4px; }
}
</style>

<style scoped>
.home-plugin-title {
  max-width: 100%;
  font-size: inherit;
  font-weight: 500;
}
</style>
