<script setup lang="ts">
import { useDelayedLoading } from '../../ui/useDelayedLoading';
import AppLoadError from '../../components/AppLoadError.vue';
import AppTooltip from '../../components/AppTooltip.vue';
import AppLoading from '../../components/AppLoading.vue';
import { NAlert, NButton, NCollapseTransition, NInput, NCard, NSwitch, NFlex, NText, NEmpty } from 'naive-ui';
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import AppLinkButton from '../../components/AppLinkButton.vue';
import {
  readApiError,
  isAbortError,
  userErrorMessage,
} from '../../api/client';
import { createPluginRequests } from './pluginRequests';
import { showToast } from '../../ui/toast';
import type { PluginInfo } from './pluginModels';

const plugins = ref<PluginInfo[]>([]);
const loading = ref(false);
const scanning = ref(false);
const hasLoaded = ref(false);
const error = ref('');
const changingIds = ref(new Set<string>());
const search = ref('');
const requests = createPluginRequests();
let loadGeneration = 0;
const filteredPlugins = computed(() => {
  const query = search.value.trim().toLowerCase();
  if (!query) return plugins.value;
  return plugins.value.filter((plugin) =>
    [
      plugin.manifest?.name,
      plugin.id,
      plugin.directoryName,
      ...(plugin.manifest?.authors ?? []).map((author) => author.name),
    ].some((value) => value?.toLowerCase().includes(query)),
  );
});
onMounted(load);
onBeforeUnmount(requests.dispose);
async function load(scan = false) {
  const generation = ++loadGeneration;
  requests.cancelReads();
  loading.value = true;
  scanning.value = scan;
  try {
    const loaded = await requests.run<PluginInfo[]>(
      scan ? '/api/plugins/scan' : '/api/plugins',
      {
        method: scan ? 'POST' : 'GET',
      },
      async (response) => {
        if (!response.ok) throw new Error(await readApiError(response, '操作失败，请稍后重试'));
        return response.json();
      },
    );
    if (generation === loadGeneration) {
      plugins.value = loaded;
      hasLoaded.value = true;
      error.value = '';
    }
  } catch (cause) {
    if (requests.active && generation === loadGeneration && !isAbortError(cause))
      error.value = userErrorMessage(cause, '无法加载插件');
  } finally {
    if (generation === loadGeneration) {
      loading.value = false;
      scanning.value = false;
    }
  }
}

async function toggle(plugin: PluginInfo) {
  if (loading.value || !plugin.canChangeEnabled || changingIds.value.has(plugin.id)) return;
  const failureMessage = plugin.enabled ? '插件禁用失败' : '插件启用失败';
  changingIds.value.add(plugin.id);
  try {
    const updated = await requests.run<PluginInfo>(
      `/api/plugins/${encodeURIComponent(plugin.id)}/${plugin.enabled ? 'disable' : 'enable'}`,
      { method: 'POST' },
      (response) => {
        if (!response.ok) throw new Error(failureMessage);
        return response.json();
      },
    );
    if (!requests.active) return;
    plugins.value = plugins.value.map((item) => (item.id === updated.id ? updated : item));
    if (updated.state === 'Failed' || updated.state === 'Invalid')
      showToast(failureMessage, 'error');
    else showToast(`插件已${updated.enabled ? '启用' : '禁用'}`, 'success');
  } catch (cause) {
    if (requests.active && !isAbortError(cause))
      showToast(failureMessage, 'error');
  } finally {
    changingIds.value.delete(plugin.id);
  }
}
const showScanLoading = useDelayedLoading(() => scanning.value);
</script>
<template>
  <section class="plugins-view load-error-layout">
    <AppLoadError :show="Boolean(error)" :retry="() => load()">{{ error }}</AppLoadError>
    <NCard
      tag="section"
      class="plugins-panel"
      header-class="plugins-panel-heading"
      header-extra-class="plugins-panel-actions"
      content-class="plugin-list-summary"
      :segmented="{ content: true }"
    >
      <template #header>
        <div class="plugins-panel-title">
          <NText tag="h2" :depth="1">插件管理</NText>
          <NText tag="p" :depth="2">管理或查看已安装的插件</NText>
        </div>
      </template>
      <template #header-extra>
        <NFlex align="center" :size="10">
          <AppLinkButton to="/plugins/market">插件市场</AppLinkButton>
          <NButton
            :loading="showScanLoading"
            :aria-busy="scanning"
            aria-label="重新扫描"
            attr-type="button"
            :disabled="(loading && !showScanLoading) || changingIds.size > 0"
            @click="load(true)"
            type="primary"
          >
            重新扫描
          </NButton>
        </NFlex>
      </template>
      <template #default>
        <div class="plugin-summary-counts">
          <NText :depth="2">
            共 <NText tag="strong" :depth="1">{{ filteredPlugins.length }}</NText> 个插件
          </NText>
        </div>
        <div class="plugin-search">
          <NInput
            v-model:value="search"
            clearable
            aria-label="搜索插件名称、ID 或作者"
            placeholder="搜索名称、ID 或作者"
            :disabled="!plugins.length"
            :input-props="{ role: 'searchbox', enterkeyhint: 'search', 'aria-label': '搜索插件名称、ID 或作者' }"
          />
        </div>
      </template>
    </NCard>
    <AppLoading v-if="loading && !hasLoaded && !error" label="正在读取插件目录" />
    <template v-else-if="plugins.length">
      <div v-if="filteredPlugins.length" class="plugin-list">
        <NCard
          tag="article"
          v-for="plugin in filteredPlugins"
          :key="plugin.directory"
          content-class="plugin-card"
        >
          <div class="plugin-card-main">
            <div class="plugin-title-line">
              <div class="plugin-title-copy">
                <h3>{{ plugin.manifest?.name ?? plugin.directoryName }}</h3>
                <p>{{ plugin.manifest?.description || '未提供插件描述' }}</p>
              </div>
            </div>
            <p class="plugin-meta">
              <span>ID：{{ plugin.id }}</span>
              <span v-if="plugin.manifest">v{{ plugin.manifest.version }}</span>
              <span v-if="plugin.manifest?.authors?.length">
                作者 {{ plugin.manifest.authors.map((author) => author.name || '—').join('、') }}
              </span>
            </p>
          </div>
          <div class="plugin-card-footer">
            <AppTooltip
              :text="!plugin.canChangeEnabled ? '无法启用' : undefined"
              :disabled-trigger="loading || !plugin.canChangeEnabled || changingIds.has(plugin.id)"
            >
              <NSwitch
                :aria-label="
                  plugin.enabled
                    ? `禁用 ${plugin.manifest?.name ?? plugin.id}`
                    : `启用 ${plugin.manifest?.name ?? plugin.id}`
                "
                :value="plugin.enabled"
                :disabled="loading || !plugin.canChangeEnabled || changingIds.has(plugin.id)"
                @update:value="toggle(plugin)"
              />
            </AppTooltip>
            <AppLinkButton :to="`/plugins/${encodeURIComponent(plugin.id)}`"> 详情 </AppLinkButton>
          </div>
          <NCollapseTransition :show="Boolean(plugin.errors.length)" :appear="false" class="plugin-card-error">
            <div class="plugin-error-feedback">
              <NAlert type="error">{{ plugin.errors[0] }}</NAlert>
            </div>
          </NCollapseTransition>
        </NCard>
      </div>
      <NCard
        v-else
        embedded
        class="plugin-empty"
        content-class="plugin-empty-content"
        :theme-overrides="{ borderColor: 'var(--border-strong)' }"
      >
        <NEmpty description="没有匹配的插件" />
      </NCard>
    </template>
    <NCard v-else-if="error" embedded><NEmpty :show-icon="false" description="无法加载插件列表" /></NCard>
    <NCard
      v-else-if="hasLoaded"
      embedded
      class="plugin-empty"
      content-class="plugin-empty-content"
      :theme-overrides="{ borderColor: 'var(--border-strong)' }"
    >
      <NEmpty description="暂无插件">
        <template #extra>
          插件文件夹
          <code>User/Plugin</code>
        </template>
      </NEmpty>
    </NCard>
  </section>
</template>

<style>
.plugins-view { width: 100%; max-width: none; }
.plugin-list { display: grid; grid-template-columns: 1fr; gap: 12px; }
.plugin-card { display: grid; grid-template-columns: minmax(0, 1fr) auto; min-height: 0; align-items: center; justify-content: space-between; column-gap: 18px; }
.plugin-card-main { display: grid; min-width: 0; align-content: center; }
.plugin-title-copy { min-width: 0; }
.plugin-title-copy h3 { letter-spacing: -.01em; }
.plugin-title-copy p { max-width: 70ch; }
.plugin-card-footer { display: flex; align-self: stretch; align-items: center; justify-content: flex-end; gap: 10px; margin-left: auto; }
.plugin-title-line { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.plugin-title-line h3 { margin: 0; font-size: 16px; }
.plugin-title-line p { margin: 6px 0 0; color: var(--text-muted); font-size: 14px; }
.plugin-title-line p, .plugin-meta, .plugin-empty-content { transition: color .3s cubic-bezier(.4, 0, .2, 1); }
.plugin-empty-content { color: var(--text-muted); }
.plugin-meta { display: flex; flex-wrap: wrap; gap: 8px 14px; margin: 14px 0 0; color: var(--text-subtle); font-size: 12px; }
.plugin-error-feedback { padding-top: 12px; }
.plugin-card-error { grid-column: 1 / -1; min-width: 0; overflow-wrap: anywhere; }
@media (max-width: 767px) {
  .plugin-card, .plugin-card-footer { min-width: 0; }
  .plugin-card { grid-template-columns: minmax(0, 1fr); }
  .plugin-card-footer { flex-wrap: wrap; margin-top: 18px; }
}
@media (max-width: 640px) {
  .plugin-card { align-items: stretch; }
  .plugin-card-footer { margin-left: 0; justify-content: space-between; }
}
</style>
