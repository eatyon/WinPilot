<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { RouterLink, useRoute, useRouter } from 'vue-router';
import { isAbortError, userErrorMessage } from '../../api/client';
import { createPluginRequests } from './PluginRequests';
import AppModal from '../../components/AppModal.vue';
import AppIcon from '../../components/AppIcon.vue';
import { showToast } from '../../ui/toast';
import { renderMarkdown } from '../../ui/markdown';
import type {
  PluginFeatureDisplaySettings,
  PluginInfo,
  PluginStatusDisplaySettings,
  PluginSettingsDisplaySettings,
} from './PluginModels';
import { stateLabel } from './PluginModels';
import PluginSettingField from './PluginSettingField.vue';
import { isPluginSetting as isDefinition, isPluginCollection as isCollection, type PluginCollectionDefinition as CollectionDefinition, type PluginSettingDefinition as SettingDefinition, type PluginFeatureDefinition } from './PluginModels';
import { safeModeEnabled, safeModeMessage } from '../security/safeMode';

type Definition = SettingDefinition | CollectionDefinition;
const capabilityItems = [
  ['status', '状态'],
  ['features', '功能'],
  ['settings', '配置'],
] as const;
function hasUi(key: (typeof capabilityItems)[number][0]) {
  return Boolean(plugin.value?.manifest?.[`${key}_ui` as 'status_ui' | 'features_ui' | 'settings_ui']);
}
const requests = createPluginRequests();
const route = useRoute();
let loadGeneration = 0;
const router = useRouter();
const plugin = ref<PluginInfo>();
const error = ref('');
const statusKeys = ref<string[]>([]);
const statusSettings = ref<PluginStatusDisplaySettings>({
  registerOnHome: true,
  statusKeys: [],
  homeTitle: '',
});

const features = ref<PluginFeatureDefinition[]>([]);
const featureSettings = ref<PluginFeatureDisplaySettings>({
  registerOnHome: true,
  featureIds: [],
  homeTitle: '',
});
const statusSettingsOpen = ref(false);
const featureSettingsOpen = ref(false);
const settingsOpen = ref(false);
const deleteOpen = ref(false);
const savingStatusSettings = ref(false);
const savingFeatureSettings = ref(false);
const savingSettings = ref(false);
const deleting = ref(false);
const selectedStatusKeys = computed({
  get: () => statusSettings.value.statusKeys,
  set: (value) => {
    statusSettings.value.statusKeys = value;
  },
});
const selectedFeatureIds = computed({
  get: () => featureSettings.value.featureIds,
  set: (value) => {
    featureSettings.value.featureIds = value;
  },
});
const settingsDisplay = ref<PluginSettingsDisplaySettings>({ registerOnHome: true });
const settingsDefinitions = ref<Definition[]>([]);
const settingsConfig = ref<Record<string, any>>({});
const readmeAvailable = ref(false);
const readmeOpen = ref(false);
const readmeLoaded = ref(false);
const readmeLoading = ref(false);
const readmeHtml = ref('');
const readmeError = ref('');
let readmeController: AbortController | undefined;

async function checkReadme(pluginId: string) {
  try {
    const response = await requests.fetch(`/api/plugins/${encodeURIComponent(pluginId)}/readme`, {
      method: 'HEAD',
    });
    readmeAvailable.value = response.ok;
  } catch {
    readmeAvailable.value = false;
  }
}

async function toggleReadme() {
  readmeOpen.value = !readmeOpen.value;
  if (!readmeOpen.value) {
    readmeController?.abort();
    readmeController = undefined;
    readmeLoading.value = false;
    return;
  }
  if (readmeLoaded.value || readmeLoading.value) return;
  const controller = new AbortController();
  readmeController = controller;
  readmeLoading.value = true;
  readmeError.value = '';
  try {
    const response = await requests.fetch(
      `/api/plugins/${encodeURIComponent(String(route.params.pluginId))}/readme`,
      { signal: controller.signal },
    );
    if (!response.ok) throw new Error('读取说明文档失败');
    const source = await requests.text(response);
    const html = await renderMarkdown(source);
    if (controller.signal.aborted) return;
    readmeHtml.value = html;
    readmeLoaded.value = true;
  } catch (cause) {
    if (!controller.signal.aborted && !isAbortError(cause)) readmeError.value = userErrorMessage(cause, '读取说明文档失败');
  } finally {
    if (readmeController === controller) {
      readmeController = undefined;
      readmeLoading.value = false;
    }
  }
}
watch(safeModeEnabled, (enabled) => {
  if (!enabled) return;
  statusSettingsOpen.value = false;
  featureSettingsOpen.value = false;
  settingsOpen.value = false;
  deleteOpen.value = false;
});
async function loadStatusSettings() {
  const id = encodeURIComponent(String(route.params.pluginId));
  const [statusResponse, settingsResponse] = await Promise.all([
    requests.fetch(`/api/plugins/${id}/status`),
    requests.fetch(`/api/plugins/${id}/status-display-settings`),
  ]);
  if (!statusResponse.ok || !settingsResponse.ok) throw new Error('读取状态展示设置失败');
  if (statusResponse.ok)
    statusKeys.value = Object.keys((await requests.json(statusResponse)) as Record<string, unknown>);
  if (settingsResponse.ok) {
    statusSettings.value = (await requests.json(settingsResponse)) as PluginStatusDisplaySettings;
  }
}

async function openStatusSettings() {
  if (safeModeEnabled.value) return;
  try {
    await loadStatusSettings();
    if (requests.active && !safeModeEnabled.value) statusSettingsOpen.value = true;
  } catch (cause) {
    if (!isAbortError(cause)) showToast(userErrorMessage(cause, '读取状态展示设置失败'), 'error');
  }
}
async function saveStatusSettings() {
  if (savingStatusSettings.value || safeModeEnabled.value) return;
  savingStatusSettings.value = true;
  try {
    const response = await requests.fetch(
      `/api/plugins/${encodeURIComponent(String(route.params.pluginId))}/status-display-settings`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(statusSettings.value),
      },
    );
    if (!response.ok) throw new Error('保存状态展示设置失败');
    statusSettings.value = (await requests.json(response)) as PluginStatusDisplaySettings;
    statusSettingsOpen.value = false;
    showToast('状态展示设置已保存', 'success');
  } catch (cause) {
    if (requests.active && !isAbortError(cause)) showToast(userErrorMessage(cause, '保存状态展示设置失败'));
  } finally {
    savingStatusSettings.value = false;
  }
}
async function loadFeatureSettings() {
  const id = encodeURIComponent(String(route.params.pluginId));
  const [featuresResponse, settingsResponse] = await Promise.all([
    requests.fetch(`/api/plugins/${id}/features-definition`),
    requests.fetch(`/api/plugins/${id}/feature-display-settings`),
  ]);
  if (!featuresResponse.ok || !settingsResponse.ok) throw new Error('读取功能展示设置失败');
  if (featuresResponse.ok)
    features.value =
      ((await requests.json(featuresResponse)) as { features?: PluginFeatureDefinition[] }).features ?? [];
  if (settingsResponse.ok) {
    featureSettings.value = (await requests.json(settingsResponse)) as PluginFeatureDisplaySettings;
  }
}
async function openFeatureSettings() {
  if (safeModeEnabled.value) return;
  try {
    await loadFeatureSettings();
    if (requests.active && !safeModeEnabled.value) featureSettingsOpen.value = true;
  } catch (cause) {
    if (!isAbortError(cause)) showToast(userErrorMessage(cause, '读取功能展示设置失败'), 'error');
  }
}
async function loadSettings() {
  const id = encodeURIComponent(String(route.params.pluginId));
  const [settingsResponse, definitionResponse, storageResponse] = await Promise.all([
    requests.fetch(`/api/plugins/${id}/settings-display-settings`),
    requests.fetch(`/api/plugins/${id}/settings-definition`),
    requests.fetch(`/api/plugins/${id}/storage`),
  ]);
  if (settingsResponse.ok)
    settingsDisplay.value = (await requests.json(settingsResponse)) as PluginSettingsDisplaySettings;
  if (!settingsResponse.ok || !definitionResponse.ok || !storageResponse.ok)
    throw new Error('读取插件设置失败');
  settingsDefinitions.value = ((await requests.json(definitionResponse)) as unknown[])
    .filter(value => isDefinition(value) || isCollection(value));
  settingsConfig.value = (await requests.json(storageResponse)) as Record<string, any>;

}
async function openSettings() {
  if (safeModeEnabled.value) return;
  try {
    await loadSettings();
    if (requests.active && !safeModeEnabled.value) settingsOpen.value = true;
  } catch (cause) {
    if (!isAbortError(cause)) showToast(userErrorMessage(cause, '读取插件设置失败'), 'error');
  }
}
async function saveSettings() {
  if (savingSettings.value || safeModeEnabled.value) return;
  savingSettings.value = true;
  let configurationSaved = false;
  try {
    const id = encodeURIComponent(String(route.params.pluginId));
    const storageResponse = await requests.fetch(`/api/plugins/${id}/storage`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(settingsConfig.value),
    });
    if (!storageResponse.ok) throw new Error('保存配置失败');
    configurationSaved = true;
    const settingsResponse = await requests.fetch(`/api/plugins/${id}/settings-display-settings`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(settingsDisplay.value),
    });
    if (!settingsResponse.ok) throw new Error('保存配置展示设置失败');
    settingsOpen.value = false;
    showToast('配置展示设置已保存', 'success');
  } catch (cause) {
    if (requests.active && !isAbortError(cause)) showToast(configurationSaved ? '插件配置已保存，展示设置保存失败' : userErrorMessage(cause, '保存插件配置失败'), 'error');
  } finally {
    savingSettings.value = false;
  }
}
async function saveFeatureSettings() {
  if (savingFeatureSettings.value || safeModeEnabled.value) return;
  savingFeatureSettings.value = true;
  try {
    const response = await requests.fetch(
      `/api/plugins/${encodeURIComponent(String(route.params.pluginId))}/feature-display-settings`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(featureSettings.value),
      },
    );
    if (!response.ok) throw new Error('保存功能展示设置失败');
    featureSettings.value = (await requests.json(response)) as PluginFeatureDisplaySettings;
    featureSettingsOpen.value = false;
    showToast('功能展示设置已保存', 'success');
  } catch (cause) {
    if (requests.active && !isAbortError(cause)) showToast(userErrorMessage(cause, '保存功能展示设置失败'));
  } finally {
    savingFeatureSettings.value = false;
  }
}
async function deletePlugin(deleteData: boolean) {
  if (safeModeEnabled.value || deleting.value) return;
  deleting.value = true;
  try {
    const response = await requests.fetch(
      `/api/plugins/${encodeURIComponent(String(route.params.pluginId))}?deleteData=${deleteData}`,
      { method: 'DELETE' },
    );
    if (!response.ok) throw new Error('删除插件失败');
    showToast(deleteData ? '插件和配置已删除' : '插件已删除，配置已保留', 'success');
    await router.push('/plugins');
  } catch (cause) {
    if (requests.active && !isAbortError(cause)) showToast(userErrorMessage(cause, '删除插件失败'));
  } finally {
    deleting.value = false;
  }
}
async function load() {
  const generation = ++loadGeneration;
  requests.cancelReads();
  readmeController?.abort();
  readmeAvailable.value = false;
  readmeLoaded.value = false;
  readmeOpen.value = false;
  statusSettingsOpen.value = featureSettingsOpen.value = settingsOpen.value = deleteOpen.value = false;
  plugin.value = undefined;
  error.value = '';
  try {
    const response = await requests.fetch(`/api/plugins/${encodeURIComponent(String(route.params.pluginId))}`);
    if (response.ok) {
      const loadedPlugin = (await requests.json(response)) as PluginInfo;
      if (!requests.active || generation !== loadGeneration) return;
      plugin.value = loadedPlugin;
      await Promise.all([
        checkReadme(loadedPlugin.id),
        loadedPlugin.manifest?.status_ui ? loadStatusSettings() : Promise.resolve(),
        loadedPlugin.manifest?.features_ui ? loadFeatureSettings() : Promise.resolve(),
        loadedPlugin.manifest?.settings_ui ? loadSettings() : Promise.resolve(),
      ]);
    } else throw new Error('无法加载插件信息');
  } catch (cause) {
    if (requests.active && generation === loadGeneration && !isAbortError(cause)) error.value = userErrorMessage(cause, '无法加载插件信息');
  }
}
onMounted(load);
watch(() => route.params.pluginId, () => { requests.cancelAll(); void load(); });
onBeforeUnmount(() => { requests.dispose(); readmeController?.abort(); });
</script>

<template>
  <section class="plugin-detail-view">
    <p v-if="error" class="status-message error">{{ error }}</p>
    <template v-else-if="plugin">
      <section class="plugin-detail-hero">
        <div>
          <p class="plugin-detail-id">{{ plugin.id }}</p>
          <h2>{{ plugin.manifest?.name ?? plugin.directoryName }}</h2>
          <p>{{ plugin.manifest?.description || '—' }}</p>
        </div>
        <div class="plugin-info-hero-actions">
          <RouterLink class="button button-secondary" :to="`/plugins/${route.params.pluginId}`">
            返回
          </RouterLink>
          <button
            class="button button-danger"
            :class="{ 'safe-mode-button': safeModeEnabled }"
            type="button"
            :disabled="safeModeEnabled"
            :title="safeModeEnabled ? safeModeMessage : undefined"
            @click="deleteOpen = true"
          >
            <AppIcon v-if="safeModeEnabled" name="lock" />
            删除
          </button>
        </div>
      </section>
      <section class="plugin-detail-grid">
        <article class="plugin-detail-panel">
          <h3>插件元数据</h3>
          <dl class="plugin-info-list">
            <div>
              <dt>作者</dt>
              <dd class="plugin-authors">
                <template v-if="plugin.manifest?.authors?.length">
                  <template v-for="(author, index) in plugin.manifest.authors" :key="index">
                    <span v-if="index" aria-hidden="true">、</span>
                    <a v-if="author.links" :href="author.links" target="_blank" rel="noopener noreferrer">
                      {{ author.name || '—' }}
                    </a>
                    <span v-else>{{ author.name || '—' }}</span>
                  </template>
                </template>
                <span v-else>—</span>
              </dd>
            </div>
            <div>
              <dt>版本</dt>
              <dd>{{ plugin.manifest?.version || '—' }}</dd>
            </div>
            <div>
              <dt>最低 WinPilot 版本</dt>
              <dd>{{ plugin.manifest?.winpilot_version || '未声明' }}</dd>
            </div>
            <div>
              <dt>入口</dt>
              <dd>{{ plugin.manifest?.main || '—' }}</dd>
            </div>
          </dl>
        </article>
        <article class="plugin-detail-panel">
          <h3>安装信息</h3>
          <dl class="plugin-info-list">
            <div>
              <dt>状态</dt>
              <dd>{{ stateLabel[plugin.state] }}</dd>
            </div>
            <div>
              <dt>插件目录</dt>
              <dd class="path-value">{{ plugin.directory }}</dd>
            </div>
            <div>
              <dt>数据目录</dt>
              <dd class="path-value">{{ plugin.dataDirectory }}</dd>
            </div>
          </dl>
        </article>
      </section>
      <section class="plugin-detail-panel">
        <h3>能力声明</h3>
        <div class="feature-list">
          <button
            v-for="[key, label] in capabilityItems"
            :key="key"
            type="button"
            :class="{
              active: hasUi(key),
              'feature-button': hasUi(key),
              'safe-mode-button': safeModeEnabled && hasUi(key),
            }"
            :disabled="!hasUi(key) || safeModeEnabled"
            :title="safeModeEnabled && hasUi(key) ? safeModeMessage : undefined"
            @click="
              key === 'status'
                ? openStatusSettings()
                : key === 'features'
                  ? openFeatureSettings()
                  : key === 'settings'
                    ? openSettings()
                    : undefined
            "
          >
            <AppIcon v-if="safeModeEnabled && hasUi(key)" name="lock" />
            {{ label }}：{{ hasUi(key) ? '已声明' : '未声明' }}
          </button>
        </div>
      </section>
      <section v-if="readmeAvailable" class="plugin-detail-panel plugin-readme-panel">
        <h3>
          <button
            class="plugin-readme-toggle"
            type="button"
            :aria-expanded="readmeOpen"
            aria-controls="plugin-readme-content"
            @click="toggleReadme"
          >
            <span>说明文档</span>
            <span class="plugin-readme-chevron" aria-hidden="true"></span>
          </button>
        </h3>
        <div v-if="readmeOpen" id="plugin-readme-content" class="plugin-readme-content">
          <p v-if="readmeLoading" class="plugin-empty">正在读取说明文档…</p>
          <p v-else-if="readmeError" class="status-message error">{{ readmeError }}</p>
          <p v-else-if="readmeLoaded && !readmeHtml" class="plugin-empty">说明文档为空</p>
          <div v-else-if="readmeLoaded" class="markdown-content" v-html="readmeHtml"></div>
        </div>
      </section>
      <AppModal v-if="statusSettingsOpen" :dismissible="!savingStatusSettings" title="状态展示设置" @close="statusSettingsOpen = false">
        <label class="toggle-control">
          <input v-model="statusSettings.registerOnHome" type="checkbox" />
          <span>注册到首页</span>
        </label>
        <label class="field-control status-display-title">
          <span>首页标题</span>
          <input
            v-model.trim="statusSettings.homeTitle"
            type="text"
            maxlength="100"
            :placeholder="`${plugin.manifest?.name ?? plugin.id} 状态`"
          />
        </label>
        <div class="status-display-options">
          <strong>状态项</strong>
          <label v-for="key in statusKeys" :key="key" class="toggle-control">
            <input v-model="selectedStatusKeys" type="checkbox" :value="key" />
            <span>{{ key }}</span>
          </label>
          <p v-if="!statusKeys.length">暂无可配置的状态项</p>
        </div>
        <template #actions>
          <button class="button button-secondary" type="button" @click="statusSettingsOpen = false">
            取消
          </button>
          <button
            class="button button-primary"
            type="button"
            :disabled="savingStatusSettings"
            @click="saveStatusSettings"
          >
            {{ savingStatusSettings ? '正在保存…' : '保存' }}
          </button>
        </template>
      </AppModal>
      <AppModal v-if="featureSettingsOpen" :dismissible="!savingFeatureSettings" title="功能展示设置" @close="featureSettingsOpen = false">
        <label class="toggle-control">
          <input v-model="featureSettings.registerOnHome" type="checkbox" />
          <span>注册到首页</span>
        </label>
        <label class="field-control status-display-title">
          <span>首页标题</span>
          <input
            v-model.trim="featureSettings.homeTitle"
            type="text"
            maxlength="100"
            :placeholder="`${plugin.manifest?.name ?? plugin.id} 功能`"
          />
        </label>
        <div class="status-display-options">
          <strong>功能项</strong>
          <label v-for="feature in features" :key="feature.id" class="toggle-control">
            <input v-model="selectedFeatureIds" type="checkbox" :value="feature.id" />
            <span>{{ feature.name }}</span>
          </label>
          <p v-if="!features.length">暂无可配置的功能项</p>
        </div>
        <template #actions>
          <button class="button button-secondary" type="button" @click="featureSettingsOpen = false">
            取消
          </button>
          <button
            class="button button-primary"
            type="button"
            :disabled="savingFeatureSettings"
            @click="saveFeatureSettings"
          >
            {{ savingFeatureSettings ? '正在保存…' : '保存' }}
          </button>
        </template>
      </AppModal>
      <AppModal v-if="settingsOpen" :dismissible="!savingSettings" title="配置展示设置" @close="settingsOpen = false">
        <label class="toggle-control">
          <input
            :checked="!settingsDisplay.registerOnHome"
            type="checkbox"
            @change="settingsDisplay.registerOnHome = !($event.target as HTMLInputElement).checked"
          />
          <span>隐藏插件主页配置</span>
        </label>
        <div class="plugin-settings-fields">
          <template v-for="setting in settingsDefinitions" :key="setting.name">
            <PluginSettingField
              v-if="isDefinition(setting)"
              :field="setting"
              v-model="settingsConfig[setting.name]"
              :field-id="`info-setting-${setting.name}`"
            />
            <PluginSettingField v-else :field="setting" v-model="settingsConfig[setting.name]" />
          </template>
          <p v-if="!settingsDefinitions.length" class="plugin-empty">暂无配置项</p>
        </div>
        <template #actions>
          <button class="button button-secondary" type="button" @click="settingsOpen = false">取消</button>
          <button
            class="button button-primary"
            type="button"
            :disabled="savingSettings"
            @click="saveSettings"
          >
            {{ savingSettings ? '正在保存…' : '保存' }}
          </button>
        </template>
      </AppModal>
      <AppModal
        v-if="deleteOpen"
        title="删除插件"
        :dismissible="!deleting"
        :dismiss-on-backdrop="!deleting"
        @close="deleteOpen = false"
      >
        <p>是否删除当前插件和配置文件</p>
        <template #actions>
          <button
            class="button button-secondary"
            type="button"
            :disabled="deleting"
            @click="deleteOpen = false"
          >
            取消
          </button>
          <button
            class="button button-danger"
            type="button"
            :disabled="deleting"
            @click="deletePlugin(false)"
          >
            仅删除插件
          </button>
          <button class="button button-danger" type="button" :disabled="deleting" @click="deletePlugin(true)">
            {{ deleting ? '正在删除…' : '删除插件和配置' }}
          </button>
        </template>
      </AppModal>
    </template>
  </section>
</template>
