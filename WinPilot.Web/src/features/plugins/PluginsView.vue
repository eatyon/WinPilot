<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { RouterLink } from 'vue-router';
import { readApiError, isAbortError, userErrorMessage, WinPilotConnectionError } from '../../api/client';
import { createPluginRequests } from './PluginRequests';
import { showToast } from '../../ui/toast';
import type { PluginInfo } from './PluginModels';

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
  error.value = '';
  try {
    const loaded = await requests.run<PluginInfo[]>(scan ? '/api/plugins/scan' : '/api/plugins', {
      method: scan ? 'POST' : 'GET',
    }, async response => {
      if (!response.ok) throw new Error(await readApiError(response, '操作失败，请稍后重试'));
      return response.json();
    });
    if (generation === loadGeneration) {
      plugins.value = loaded;
      hasLoaded.value = true;
    }
  } catch (cause) {
    if (requests.active && generation === loadGeneration && !isAbortError(cause)) error.value = userErrorMessage(cause, '无法加载插件');
  } finally {
    if (generation === loadGeneration) {
      loading.value = false;
      scanning.value = false;
    }
  }
}
function onToggle(event: Event, plugin: PluginInfo) {
  (event.target as HTMLInputElement).checked = plugin.enabled;
  return toggle(plugin);
}

async function toggle(plugin: PluginInfo) {
  if (loading.value || !plugin.canChangeEnabled || changingIds.value.has(plugin.id)) return;
  const failureMessage = plugin.enabled ? '插件禁用失败' : '插件启用失败';
  changingIds.value.add(plugin.id);
  try {
    const updated = await requests.run<PluginInfo>(
      `/api/plugins/${encodeURIComponent(plugin.id)}/${plugin.enabled ? 'disable' : 'enable'}`,
      { method: 'POST' },
      response => {
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
    if (requests.active && !isAbortError(cause) && !(cause instanceof WinPilotConnectionError))
      showToast(failureMessage, 'error');
  } finally {
    changingIds.value.delete(plugin.id);
  }
}
</script>
<template>
  <section class="plugins-view">
    <section class="plugins-panel">
      <header class="plugins-heading">
        <div>
          <h2>插件管理</h2>
          <p>管理或查看已安装的插件</p>
        </div>
        <div class="plugins-heading-actions">
          <RouterLink class="button button-secondary" to="/plugins/market">插件市场</RouterLink>
          <button class="button button-secondary" type="button" :disabled="loading || changingIds.size > 0" @click="load(true)">
            {{ scanning ? '正在扫描…' : '重新扫描' }}
          </button>
        </div>
      </header>
      <div class="plugin-list-summary">
        <div class="plugin-summary-counts">
          <span>
            共
            <strong>{{ plugins.length }}</strong>
            个插件
          </span>
        </div>
        <div class="plugin-search">
          <input
            v-model="search"
            class="text-input"
            type="search"
            aria-label="搜索插件名称、ID 或作者"
            placeholder="搜索名称、ID 或作者"
            :disabled="!plugins.length"
          />
        </div>
      </div>
    </section>
    <p v-if="error" class="status-message error" role="alert">{{ error }}</p>
    <p v-if="loading && !hasLoaded" class="plugin-empty loading-hint">正在读取插件目录…</p>
    <template v-else-if="plugins.length">
      <div v-if="filteredPlugins.length" class="plugin-list">
        <article v-for="plugin in filteredPlugins" :key="plugin.directory" class="plugin-card">
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
            <p v-if="plugin.errors.length" class="plugin-error">{{ plugin.errors[0] }}</p>
          </div>
          <div class="plugin-card-footer">
            <label
              class="task-row-toggle"
              :title="!plugin.canChangeEnabled ? '无法启用' : plugin.enabled ? '禁用插件' : '启用插件'"
            >
              <input
                type="checkbox"
                :aria-label="
                  plugin.enabled
                    ? `禁用 ${plugin.manifest?.name ?? plugin.id}`
                    : `启用 ${plugin.manifest?.name ?? plugin.id}`
                "
                :checked="plugin.enabled"
                :disabled="loading || !plugin.canChangeEnabled || changingIds.has(plugin.id)"
                @change="onToggle($event, plugin)"
              />
            </label>
            <RouterLink class="button button-secondary" :to="`/plugins/${encodeURIComponent(plugin.id)}`">
              详情
            </RouterLink>
          </div>
        </article>
      </div>
      <div v-else class="plugin-empty">
        <strong>没有匹配的插件</strong>
        <p>尝试搜索插件名称、ID 或作者</p>
      </div>
    </template>
    <div v-else-if="hasLoaded && !error && !loading" class="plugin-empty">
      <strong>暂无插件</strong>
      <p>
        插件文件夹
        <code>User/Plugin</code>
      </p>
    </div>
  </section>
</template>
