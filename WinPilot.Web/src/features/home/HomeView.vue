<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { RouterLink, useRouter } from 'vue-router';
import { apiFetch, isAbortError, userErrorMessage } from '../../api/client';
import AppModal from '../../components/AppModal.vue';
import AppSelect from '../../components/AppSelect.vue';
import { showToast } from '../../ui/toast';
import { getPageScroller } from '../../ui/scrollLock';

type IntervalOption = '1' | '3' | '5' | '10' | 'custom';
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
type PluginStatusDisplaySettings = { registerOnHome: boolean; statusKeys: string[]; homeTitle: string };
type PluginFeature = {
  id: string;
  name: string;
  description?: string;
  mode: 'action' | 'page';
  entry?: string;
  confirm?: boolean;
};
type PluginFeatureSummary = { id: string; name: string; homeTitle: string; features: PluginFeature[] };
type PluginFeatureDisplaySettings = { registerOnHome: boolean; featureIds: string[]; homeTitle: string };

const status = ref<SystemStatus>();
const errorMessage = ref<string>();
const isRefreshing = ref(false);
const autoEnabled = ref(false);
const intervalOption = ref<IntervalOption>('5');
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
const statusProvider = ref<PluginSummary>();
const pluginFeatureSummaries = ref<PluginFeatureSummary[]>([]);
const featureProvider = ref<PluginFeatureSummary>();
const executingFeatureKey = ref('');
const confirmingFeature = ref<{ plugin: PluginFeatureSummary; feature: PluginFeature }>();
const pluginStateEvents = new Map<string, EventSource>();
const router = useRouter();
const restoringScroll = ref(Number(sessionStorage.getItem('winpilot-home-scroll-y')) > 0);

const isCustomIntervalValid = computed(
  () =>
    Number.isInteger(customIntervalSeconds.value) &&
    customIntervalSeconds.value >= 1 &&
    customIntervalSeconds.value <= 600,
);
const activeIntervalMilliseconds = computed(() => {
  if (!autoEnabled.value) return 0;
  const seconds =
    intervalOption.value === 'custom' ? customIntervalSeconds.value : Number(intervalOption.value);
  return intervalOption.value === 'custom' && !isCustomIntervalValid.value ? 0 : seconds * 1000;
});
const relativeCapturedAt = computed(() => {
  if (!capturedAt.value) return '尚未刷新';
  const elapsedSeconds = Math.max(0, Math.floor((currentTime.value - capturedAt.value.getTime()) / 1000));
  if (elapsedSeconds === 0) return '刚刚';
  if (elapsedSeconds < 60) return `${elapsedSeconds} 秒前`;
  if (elapsedSeconds >= 3600) return '1 小时前';
  return `${Math.floor(elapsedSeconds / 60)} 分钟前`;
});
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
async function refreshStatus() {
  if (!isPageActive || isRefreshing.value) return;
  clearAutomaticRefresh();
  statusController?.abort();
  const controller = new AbortController();
  statusController = controller;
  isRefreshing.value = true;
  errorMessage.value = undefined;
  try {
    const response = await apiFetch('/api/system/status', { cache: 'no-store', signal: controller.signal });
    if (!response.ok) throw new Error('系统状态获取失败');
    const nextStatus = (await response.json()) as SystemStatus;
    if (!isPageActive || statusController !== controller) return;
    status.value = nextStatus;
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
    const response = await apiFetch('/api/plugins', { cache: 'no-store', signal: controller.signal });
    if (!response.ok || !isPageActive || pluginController !== controller) return;
    const plugins = (await response.json()) as Array<{
      id: string;
      manifest?: { name?: string; status_ui?: boolean; features_ui?: string };
      state: string;
      enabled: boolean;
    }>;
    if (!isPageActive || pluginController !== controller) return;
    pluginStateEvents.forEach((source) => source.close());
    pluginStateEvents.clear();
    const statusKeysByPlugin = new Map<string, string[]>();
    const summaries = await Promise.all(
      plugins
        .filter((plugin) => plugin.enabled && plugin.manifest?.status_ui)
        .map(async (plugin) => {
          const encodedId = encodeURIComponent(plugin.id);
          const [result, settingsResult] = await Promise.all([
            apiFetch(`/api/plugins/${encodedId}/status`, { cache: 'no-store', signal: controller.signal }),
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
    if (!isPageActive || pluginController !== controller || controller.signal.aborted) return;
    pluginSummaries.value = summaries.filter((item): item is PluginSummary => item !== undefined);
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
        } catch {
        }
      };
      pluginStateEvents.set(plugin.id, source);
    }
    const featureSummaries = await Promise.all(
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
    if (!isPageActive || pluginController !== controller || controller.signal.aborted) return;
    pluginFeatureSummaries.value = featureSummaries.filter(
      (item): item is PluginFeatureSummary => item !== undefined,
    );
  } catch (error) {
    if (isPageActive && pluginController === controller && !isAbortError(error)) {
      pluginStateEvents.forEach((source) => source.close());
      pluginStateEvents.clear();
      pluginSummaries.value = [];
      pluginFeatureSummaries.value = [];
    }
  } finally {
    if (pluginController === controller) pluginController = undefined;
  }
}

async function executeFeature(plugin: PluginFeatureSummary, feature: PluginFeature): Promise<boolean> {
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
    void router.push(`/plugins/${encodeURIComponent(plugin.id)}/features/${encodeURIComponent(feature.id)}`);
    return;
  }
  if (feature.mode !== 'action') return;
  if (feature.confirm === false) void executeFeature(plugin, feature);
  else confirmingFeature.value = { plugin, feature };
}

async function confirmFeature() {
  const current = confirmingFeature.value;
  if (!current || executingFeatureKey.value) return;
  if (await executeFeature(current.plugin, current.feature)) confirmingFeature.value = undefined;
}

function clearAutomaticRefresh() {
  if (refreshTimer !== undefined) {
    window.clearTimeout(refreshTimer);
    refreshTimer = undefined;
  }
}
function configureAutomaticRefresh() {
  clearAutomaticRefresh();
  if (!isPageActive || document.visibilityState !== 'visible' || isRefreshing.value || activeIntervalMilliseconds.value <= 0) return;
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
  if (relativeTimeTimer === undefined && capturedAt.value && document.visibilityState === 'visible'
    && currentTime.value - capturedAt.value.getTime() < 3600000) {
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
  pluginStateEvents.forEach((source) => source.close());
  pluginStateEvents.clear();
  if (relativeTimeTimer !== undefined) window.clearInterval(relativeTimeTimer);
  relativeTimeTimer = undefined;
  document.removeEventListener('visibilitychange', handlePageResume);
  window.removeEventListener('focus', handlePageResume);
  statusController?.abort();
  statusController = undefined;
  featureController?.abort();
  status.value = undefined;
});
</script>

<template>
  <section class="system-status-view" aria-label="系统状态" :style="restoringScroll ? { visibility: 'hidden' } : undefined">
    <section class="system-status-panel">
      <div class="system-status-heading">
        <div class="system-refresh-controls">
          <label class="toggle-control">
            <input v-model="autoEnabled" type="checkbox" />
            <span>自动刷新</span>
          </label>
          <label class="field-control interval-control">
            <span>间隔</span>
            <AppSelect
              v-model="intervalOption"
              :options="[
                { value: '1', label: '1 秒' },
                { value: '3', label: '3 秒' },
                { value: '5', label: '5 秒' },
                { value: '10', label: '10 秒' },
                { value: 'custom', label: '自定义' },
              ]"
            />
          </label>
          <label
            v-if="intervalOption === 'custom'"
            class="field-control interval-control custom-interval-control"
          >
            <span>秒</span>
            <input
              v-model.number="customIntervalSeconds"
              type="number"
              min="1"
              max="600"
              step="1"
              inputmode="numeric"
              :aria-invalid="!isCustomIntervalValid"
            />
          </label>
          <p
            v-if="intervalOption === 'custom' && !isCustomIntervalValid"
            class="interval-validation"
            role="alert"
          >
            请输入 1 至 600 的整数秒数
          </p>
        </div>
        <div class="system-status-actions">
          <span class="capture-time">
            最近刷新：
            <strong>{{ relativeCapturedAt }}</strong>
          </span>
          <button
            class="button button-primary capture-button"
            type="button"
            :disabled="isRefreshing"
            @click="refreshStatus"
          >
            {{ isRefreshing ? '正在刷新…' : '刷新' }}
          </button>
        </div>
      </div>
      <div class="system-status-grid" :aria-busy="isRefreshing">
        <section class="system-status-card">
          <div v-for="item in cpuMemoryItems" :key="item.name" class="system-status-primary-item">
            <p>{{ item.name }}</p>
            <strong :class="{ unavailable: !item.available }">{{ item.value }}</strong>
          </div>
        </section>
        <section class="system-status-card">
          <h2>GPU</h2>
          <dl class="system-status-resource-list">
            <div v-for="item in gpuItems" :key="item.name">
              <dt>{{ item.name }}</dt>
              <dd :class="{ unavailable: !item.available }">{{ item.value }}</dd>
            </div>
          </dl>
        </section>
        <section class="system-status-card">
          <h2>磁盘</h2>
          <dl class="system-status-resource-list">
            <div v-for="item in diskItems" :key="item.name">
              <dt>{{ item.name }}</dt>
              <dd :class="{ unavailable: !item.available }">{{ item.value }}</dd>
            </div>
          </dl>
        </section>
      </div>
    </section>
    <p v-if="errorMessage" class="status-message error" role="alert">{{ errorMessage }}</p>
    <section
      v-if="pluginSummaries.length || pluginFeatureSummaries.length"
      class="home-plugin-modules"
      aria-label="插件内容"
    >
      <article v-for="plugin in pluginSummaries" :key="plugin.id" class="home-plugin-module">
        <header class="home-plugin-module-heading">
          <button class="home-plugin-module-title" type="button" @click="statusProvider = plugin">
            {{ plugin.homeTitle }}
          </button>
        </header>
        <dl v-if="Object.keys(plugin.status).length" class="home-plugin-status-list">
          <div v-for="[key, value] in Object.entries(plugin.status)" :key="key">
            <dt>{{ key }}</dt>
            <dd>{{ displayPluginStatus(value) }}</dd>
          </div>
        </dl>
        <p v-else class="home-plugin-status-empty">暂无状态数据</p>
      </article>
      <article
        v-for="plugin in pluginFeatureSummaries"
        :key="`${plugin.id}-features`"
        class="home-plugin-module"
      >
        <header class="home-plugin-module-heading">
          <button class="home-plugin-module-title" type="button" @click="featureProvider = plugin">
            {{ plugin.homeTitle }}
          </button>
        </header>
        <div class="plugin-features-list">
          <article v-for="feature in plugin.features" :key="feature.id" class="plugin-feature-item">
            <div>
              <h4>{{ feature.name }}</h4>
              <p v-if="feature.description">{{ feature.description }}</p>
            </div>
            <div class="plugin-feature-controls">
              <button
                class="button button-secondary"
                type="button"
                :aria-label="`${featureButtonLabel(feature)}${feature.name}`"
                :disabled="executingFeatureKey === `${plugin.id}:${feature.id}`"
                @click="beginFeature(plugin, feature)"
              >
                {{
                  executingFeatureKey === `${plugin.id}:${feature.id}`
                    ? '正在打开…'
                    : featureButtonLabel(feature)
                }}
              </button>
            </div>
          </article>
        </div>
      </article>
    </section>
    <AppModal v-if="statusProvider" title="状态来源" @close="statusProvider = undefined">
      <p>此状态栏由插件“{{ statusProvider.name }}”提供</p>
      <template #actions>
        <RouterLink
          class="button button-secondary"
          :to="`/plugins/${encodeURIComponent(statusProvider.id)}`"
          @click="statusProvider = undefined"
        >
          进入插件主页
        </RouterLink>
        <RouterLink
          class="button button-primary"
          :to="`/plugins/${encodeURIComponent(statusProvider.id)}/info`"
          @click="statusProvider = undefined"
        >
          进入插件状态管理
        </RouterLink>
      </template>
    </AppModal>
    <AppModal v-if="featureProvider" title="功能来源" @close="featureProvider = undefined">
      <p>此功能栏由插件“{{ featureProvider.name }}”提供</p>
      <template #actions>
        <RouterLink
          class="button button-secondary"
          :to="`/plugins/${encodeURIComponent(featureProvider.id)}`"
          @click="featureProvider = undefined"
        >
          进入插件主页
        </RouterLink>
        <RouterLink
          class="button button-primary"
          :to="`/plugins/${encodeURIComponent(featureProvider.id)}/info`"
          @click="featureProvider = undefined"
        >
          进入插件功能管理
        </RouterLink>
      </template>
    </AppModal>
    <AppModal
      v-if="confirmingFeature"
      title="提示"
      :dismiss-on-backdrop="!executingFeatureKey"
      @close="confirmingFeature = undefined"
    >
      <p>确定要执行“{{ confirmingFeature.feature.name }}”吗？</p>
      <template #actions>
        <button
          class="button button-secondary"
          type="button"
          :disabled="Boolean(executingFeatureKey)"
          @click="confirmingFeature = undefined"
        >
          取消
        </button>
        <button
          class="button button-primary"
          type="button"
          :disabled="Boolean(executingFeatureKey)"
          @click="confirmFeature"
        >
          {{ executingFeatureKey ? '正在执行…' : '确认执行' }}
        </button>
      </template>
    </AppModal>
  </section>
</template>
