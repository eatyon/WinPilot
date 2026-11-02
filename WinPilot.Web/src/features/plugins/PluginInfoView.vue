<script setup lang="ts">
import AppLoadError from '../../components/AppLoadError.vue';
import { useModalState } from '../../ui/useModalState';
import { useDelayedLoading } from '../../ui/useDelayedLoading';
import AppLoading from '../../components/AppLoading.vue';
import AppLinkButton from '../../components/AppLinkButton.vue';
import { NEmpty, NFormItem, NButton, NInput, NCheckbox, NCheckboxGroup, NSpace, NCard } from 'naive-ui';
import { useId, computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { readApiError, isAbortError, userErrorMessage } from '../../api/client';
import { createPluginRequests } from './pluginRequests';
import AppModal from '../../components/AppModal.vue';
import { showToast } from '../../ui/toast';
import { renderMarkdown } from '../../ui/markdown';
import {
  type PluginFeatureDisplaySettings,
  type PluginInfo,
  type PluginStatusDisplaySettings,
  type PluginSettingsDisplaySettings,
  stateLabel,
  isPluginSetting,
  isPluginCollection,
  type PluginConfigurationDefinition,
  type PluginFeatureDefinition,
} from './pluginModels';
import PluginSettingField from './PluginSettingField.vue';
import { safeModeEnabled } from '../security/safeMode';

const formId = useId();


const capabilityItems = [
  ['status', '状态'],
  ['features', '功能'],
  ['settings', '配置'],
] as const;
function hasUi(key: (typeof capabilityItems)[number][0]) {
  return Boolean(
    plugin.value?.manifest?.[`${key}_ui` as 'status_ui' | 'features_ui' | 'settings_ui'],
  );
}
const requests = createPluginRequests();
const route = useRoute();
let loadGeneration = 0;
const router = useRouter();
const plugin = ref<PluginInfo>();
const pageElement = ref<HTMLElement | null>(null);
const highlightedCapability = ref('');
watch([plugin, () => route.query.focus], () => {
  highlightedCapability.value = '';
  const target = route.query.focus;
  if (!requests.active || !plugin.value || safeModeEnabled.value || (target !== 'status' && target !== 'features') || !hasUi(target)) return;
  const entry = pageElement.value?.querySelector<HTMLButtonElement>(`[data-capability="${target}"]`);
  entry?.scrollIntoView({ block: 'center', inline: 'nearest' });
  entry?.focus({ preventScroll: true });
  if (entry) highlightedCapability.value = target;
}, { flush: 'post' });
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
const statusSettingsOpenModal = useModalState(() => false);
const statusSettingsOpen = statusSettingsOpenModal.data;
const featureSettingsOpenModal = useModalState(() => false);
const featureSettingsOpen = featureSettingsOpenModal.data;
const settingsOpenModal = useModalState(() => false);
const settingsOpen = settingsOpenModal.data;
const deleteOpenModal = useModalState(() => false);
const deleteOpen = deleteOpenModal.data;
const savingStatusSettings = ref(false);
const savingFeatureSettings = ref(false);
const savingSettings = ref(false);
const loadingCapability = ref<'status' | 'features' | 'settings' | ''>('');
const showCapabilityLoading = useDelayedLoading(() => Boolean(loadingCapability.value));
let capabilityGeneration = 0;
const deleting = ref(false);
const deletingData = ref<boolean>();
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
const settingsDefinitions = ref<PluginConfigurationDefinition[]>([]);
const settingsConfig = ref<Record<string, unknown>>({});
const readmeAvailable = computed(() => plugin.value?.hasReadme === true);
const readmeOpenModal = useModalState(() => false, Boolean, clearReadmeContent);
const readmeOpen = readmeOpenModal.data;
const readmeLoading = ref(false);
const showReadmeLoading = useDelayedLoading(() => readmeLoading.value);
const readmeHtml = ref('');
let readmeController: AbortController | undefined;
let settingsController: AbortController | undefined;

function closeReadme() {
  readmeController?.abort();
  readmeController = undefined;
  readmeLoading.value = false;
  readmeOpenModal.close();
}
function clearReadmeContent() {
  readmeLoading.value = false;
  readmeHtml.value = '';
}

async function openReadme() {
  if (!requests.active || !readmeAvailable.value || readmeOpenModal.show || readmeLoading.value) return;
  clearReadmeContent();
  return loadReadme();
}

async function loadReadme() {
  readmeController?.abort();
  const controller = new AbortController();
  readmeController = controller;
  readmeLoading.value = true;
  try {
    const source = await requests.run(
      `/api/plugins/${encodeURIComponent(String(route.params.pluginId))}/readme`,
      { signal: controller.signal },
      (response) => {
        if (!response.ok) throw new Error('读取说明文档失败');
        return response.text();
      },
    );
    if (controller.signal.aborted) return;
    const html = await renderMarkdown(source);
    if (controller.signal.aborted) return;
    readmeHtml.value = html;
    readmeOpenModal.open(true);
  } catch (cause) {
    if (requests.active && !controller.signal.aborted && !isAbortError(cause))
      showToast(userErrorMessage(cause, '读取说明文档失败'), 'error');
  } finally {
    if (readmeController === controller) {
      readmeController = undefined;
      readmeLoading.value = false;
    }
  }
}
watch(
  safeModeEnabled,
  (enabled) => {
    settingsController?.abort();
    capabilityGeneration++;
    loadingCapability.value = '';
    settingsConfig.value = {};
    settingsDefinitions.value = [];
    if (!enabled) return;
    statusSettingsOpenModal.reset();
    featureSettingsOpenModal.reset();
    settingsOpenModal.reset();
    deleteOpenModal.reset();
  },
  { flush: 'sync' },
);
async function loadStatusSettings() {
  const id = encodeURIComponent(String(route.params.pluginId));
  const [status, settings] = await Promise.all([
    requests.run<Record<string, unknown>>(`/api/plugins/${id}/status`, {}, (response) => {
      if (!response.ok) throw new Error('读取状态展示设置失败');
      return response.json();
    }),
    requests.run<PluginStatusDisplaySettings>(
      `/api/plugins/${id}/settings/display/status`,
      {},
      (response) => {
        if (!response.ok) throw new Error('读取状态展示设置失败');
        return response.json();
      },
    ),
  ]);
  statusKeys.value = Object.keys(status);
  statusSettings.value = settings;
}

async function openCapability(
  key: 'status' | 'features' | 'settings',
  load: () => Promise<void>,
  open: () => void,
  fallback: string,
) {
  if (!requests.active || safeModeEnabled.value || loadingCapability.value) return;
  const generation = ++capabilityGeneration;
  loadingCapability.value = key;
  try {
    await load();
    if (requests.active && !safeModeEnabled.value && generation === capabilityGeneration) open();
  } catch (cause) {
    if (requests.active && generation === capabilityGeneration && !isAbortError(cause))
      showToast(userErrorMessage(cause, fallback), 'error');
  } finally {
    if (generation === capabilityGeneration) loadingCapability.value = '';
  }
}
function openStatusSettings() {
  return openCapability('status', loadStatusSettings, () => statusSettingsOpenModal.open(true), '读取状态展示设置失败');
}
async function saveStatusSettings() {
  if (savingStatusSettings.value || safeModeEnabled.value) return;
  savingStatusSettings.value = true;
  try {
    statusSettings.value = await requests.run<PluginStatusDisplaySettings>(
      `/api/plugins/${encodeURIComponent(String(route.params.pluginId))}/settings/display/status`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(statusSettings.value),
      },
      (response) => {
        if (!response.ok) throw new Error('保存状态展示设置失败');
        return response.json();
      },
    );
    statusSettingsOpenModal.close();
    showToast('状态展示设置已保存', 'success');
  } catch (cause) {
    if (requests.active && !isAbortError(cause))
      showToast(userErrorMessage(cause, '保存状态展示设置失败'));
  } finally {
    savingStatusSettings.value = false;
  }
}
async function loadFeatureSettings() {
  const id = encodeURIComponent(String(route.params.pluginId));
  const [declaredFeatures, settings] = await Promise.all([
    requests.run<{ features?: PluginFeatureDefinition[] }>(
      `/api/plugins/${id}/features`,
      {},
      (response) => {
        if (!response.ok) throw new Error('读取功能展示设置失败');
        return response.json();
      },
    ),
    requests.run<PluginFeatureDisplaySettings>(
      `/api/plugins/${id}/settings/display/features`,
      {},
      (response) => {
        if (!response.ok) throw new Error('读取功能展示设置失败');
        return response.json();
      },
    ),
  ]);
  features.value = declaredFeatures.features ?? [];
  featureSettings.value = settings;
}
function openFeatureSettings() {
  return openCapability('features', loadFeatureSettings, () => featureSettingsOpenModal.open(true), '读取功能展示设置失败');
}
async function loadSettings() {
  settingsController?.abort();
  settingsController = new AbortController();
  const signal = settingsController.signal;
  const id = encodeURIComponent(String(route.params.pluginId));
  const [display, definitions, configuration] = await Promise.all([
    requests.run<PluginSettingsDisplaySettings>(
      `/api/plugins/${id}/settings/display/configuration`,
      { signal },
      (response) => {
        if (!response.ok) throw new Error('读取插件设置失败');
        return response.json();
      },
    ),
    requests.run<unknown[]>(`/api/plugins/${id}/configuration/schema`, { signal }, (response) => {
      if (!response.ok) throw new Error('读取插件设置失败');
      return response.json();
    }),
    requests.run<Record<string, unknown>>(
      `/api/plugins/${id}/configuration`,
      { signal },
      (response) => {
        if (!response.ok) throw new Error('读取插件设置失败');
        return response.json();
      },
    ),
  ]);
  settingsDisplay.value = display;
  settingsDefinitions.value = definitions.filter(
    (value) => isPluginSetting(value) || isPluginCollection(value),
  );
  settingsConfig.value = configuration;
}
function openSettings() {
  return openCapability('settings', loadSettings, () => settingsOpenModal.open(true), '读取插件设置失败');
}
async function saveSettings() {
  if (savingSettings.value || safeModeEnabled.value) return;
  savingSettings.value = true;
  let configurationSaved = false;
  try {
    const id = encodeURIComponent(String(route.params.pluginId));
    await requests.run(
      `/api/plugins/${id}/configuration`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(settingsConfig.value),
      },
      async (response) => {
        if (!response.ok) throw new Error(await readApiError(response, '配置保存失败，请重试'));
      },
    );
    configurationSaved = true;
    await requests.run(
      `/api/plugins/${id}/settings/display/configuration`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(settingsDisplay.value),
      },
      (response) => {
        if (!response.ok) throw new Error('保存配置展示设置失败');
      },
    );
    settingsOpenModal.close();
    showToast('配置展示设置已保存', 'success');
  } catch (cause) {
    if (requests.active && !isAbortError(cause))
      showToast(
        configurationSaved
          ? '配置已保存，展示设置保存失败，请重试'
          : userErrorMessage(cause, '配置保存失败，请重试'),
        'error',
      );
  } finally {
    savingSettings.value = false;
  }
}
async function saveFeatureSettings() {
  if (savingFeatureSettings.value || safeModeEnabled.value) return;
  savingFeatureSettings.value = true;
  try {
    featureSettings.value = await requests.run<PluginFeatureDisplaySettings>(
      `/api/plugins/${encodeURIComponent(String(route.params.pluginId))}/settings/display/features`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(featureSettings.value),
      },
      (response) => {
        if (!response.ok) throw new Error('保存功能展示设置失败');
        return response.json();
      },
    );
    featureSettingsOpenModal.close();
    showToast('功能展示设置已保存', 'success');
  } catch (cause) {
    if (requests.active && !isAbortError(cause))
      showToast(userErrorMessage(cause, '保存功能展示设置失败'));
  } finally {
    savingFeatureSettings.value = false;
  }
}
async function deletePlugin(deleteData: boolean) {
  if (safeModeEnabled.value || deleting.value) return;
  deleting.value = true;
  deletingData.value = deleteData;
  try {
    await requests.run(
      `/api/plugins/${encodeURIComponent(String(route.params.pluginId))}?deleteData=${deleteData}`,
      { method: 'DELETE' },
      (response) => {
        if (!response.ok) throw new Error('删除插件失败');
      },
    );
    showToast(deleteData ? '插件和配置已删除' : '插件已删除，配置已保留', 'success');
    await router.push('/plugins');
  } catch (cause) {
    if (requests.active && !isAbortError(cause)) showToast(userErrorMessage(cause, '删除插件失败'));
  } finally {
    deleting.value = false;
    deletingData.value = undefined;
  }
}
async function load() {
  const generation = ++loadGeneration;
  capabilityGeneration++;
  loadingCapability.value = '';
  requests.cancelReads();
  closeReadme();
  readmeOpenModal.reset();
  statusSettingsOpenModal.reset();
  featureSettingsOpenModal.reset();
  settingsOpenModal.reset();
  deleteOpenModal.reset();
  try {
    const loadedPlugin = await requests.run<PluginInfo>(
      `/api/plugins/${encodeURIComponent(String(route.params.pluginId))}`,
      {},
      (response) => {
        if (!response.ok) throw new Error('无法加载插件信息');
        return response.json();
      },
    );
    if (!requests.active || generation !== loadGeneration) return;
    plugin.value = loadedPlugin;
    if (requests.active && generation === loadGeneration) error.value = '';
  } catch (cause) {
    if (requests.active && generation === loadGeneration && !isAbortError(cause))
      error.value = userErrorMessage(cause, '无法加载插件信息');
  }
}
onMounted(load);
watch(
  () => route.params.pluginId,
  () => {
    error.value = '';
    plugin.value = undefined;
    requests.cancelAll();
    void load();
  },
);
onBeforeUnmount(() => {
  requests.dispose();
  closeReadme();
  readmeOpenModal.reset();
});
</script>

<template>
  <section ref="pageElement" class="plugin-detail-view load-error-layout">
    <AppLoadError :show="Boolean(error)" :retry="load">{{ error }}</AppLoadError>
    <AppLoading v-if="!plugin && !error" label="正在读取插件信息" />
    <NCard v-if="!plugin && error"><NEmpty :show-icon="false" description="无法加载插件信息" /></NCard>
    <template v-if="plugin">
      <NCard tag="section" content-class="plugin-detail-hero">
        <div>
          <p class="plugin-detail-id">{{ plugin.id }}</p>
          <h2>{{ plugin.manifest?.name ?? plugin.directoryName }}</h2>
          <p>{{ plugin.manifest?.description || '—' }}</p>
        </div>
        <div class="plugin-info-hero-actions">
          <AppLinkButton :to="`/plugins/${route.params.pluginId}`"> 返回 </AppLinkButton>
          <NButton
            attr-type="button"
            :disabled="safeModeEnabled"
            @click="deleteOpenModal.open(true)"
            type="error"
          >
            删除
          </NButton>
        </div>
      </NCard>
      <section class="plugin-detail-grid">
        <NCard tag="article" content-class="plugin-detail-panel">
          <h3>插件元数据</h3>
          <dl class="plugin-info-list">
            <div>
              <dt>作者</dt>
              <dd class="plugin-authors">
                <template v-if="plugin.manifest?.authors?.length">
                  <template v-for="(author, index) in plugin.manifest.authors" :key="index">
                    <span v-if="index" aria-hidden="true">、</span>
                    <a
                      v-if="author.links"
                      :href="author.links"
                      target="_blank"
                      rel="noopener noreferrer"
                    >
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
        </NCard>
        <NCard tag="article" content-class="plugin-detail-panel">
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
        </NCard>
      </section>
      <NCard tag="section" content-class="plugin-detail-panel">
        <h3>能力声明</h3>
        <div class="feature-list">
          <template v-for="[key, label] in capabilityItems" :key="key">
            <NButton
              :data-capability="key"
              :class="{ 'capability-arrival': highlightedCapability === key }"
              @animationend="(event: AnimationEvent) => { if (event.target === event.currentTarget) highlightedCapability = ''; }"
              :type="hasUi(key) ? 'primary' : 'default'"
              :secondary="hasUi(key)"
              :loading="loadingCapability === key && showCapabilityLoading"
              :aria-busy="loadingCapability === key"
              :disabled="!hasUi(key) || safeModeEnabled || Boolean(loadingCapability && (loadingCapability !== key || !showCapabilityLoading))"
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
              {{ label }}：{{ hasUi(key) ? '已声明' : '未声明' }}
            </NButton>
          </template>
        </div>
      </NCard>
      <NCard tag="section" v-if="readmeAvailable" content-class="plugin-detail-panel">
        <div class="content-section-header">
          <h3>说明文档</h3>
          <NButton
            attr-type="button"
            aria-label="查看说明文档"
            aria-haspopup="dialog"
            :loading="showReadmeLoading"
            :aria-busy="readmeLoading"
            :disabled="readmeLoading && !showReadmeLoading"
            @click="openReadme"
          >查看</NButton
          >
        </div>
      </NCard>
      <AppModal
        :show="readmeOpenModal.show"
        @after-leave="readmeOpenModal.afterLeave"
        v-if="readmeOpen"
        title="说明文档"
        size="medium"
        @close="closeReadme"
      >
        <div v-if="readmeHtml" class="markdown-content" v-html="readmeHtml"></div>
        <NEmpty :show-icon="false" v-else class="plugin-empty">说明文档为空</NEmpty>
        <template #actions>
          <NButton attr-type="button" @click="closeReadme">关闭</NButton>
        </template>
      </AppModal>
      <AppModal
        :show="statusSettingsOpenModal.show"
        @after-leave="statusSettingsOpenModal.afterLeave"
        v-if="statusSettingsOpen"
        :dismissible="!savingStatusSettings"
        title="状态展示设置"
        @close="statusSettingsOpenModal.close()"
      >
        <div :inert="savingStatusSettings">
          <NCheckbox v-model:checked="statusSettings.registerOnHome">注册到首页</NCheckbox>
          <NFormItem
            label="首页标题"
            :label-props="{ for: `${formId}-field-0` }"
            :show-feedback="false"
            class="form-field status-display-title"
          >
            <NInput
              :input-props="{ id: `${formId}-field-0` }"
              :value="statusSettings.homeTitle"
              @update:value="statusSettings.homeTitle = $event.trim()"
              maxlength="100"
              :placeholder="`${plugin.manifest?.name ?? plugin.id} 状态`"
            />
          </NFormItem>
          <div class="status-display-options">
            <strong>状态项</strong>
            <NCheckboxGroup
              v-model:value="selectedStatusKeys"
            ><NSpace vertical
              ><NCheckbox v-for="key in statusKeys" :key="key" :value="key">{{
                  key
                }}</NCheckbox></NSpace
              ></NCheckboxGroup
            >
            <NEmpty v-if="!statusKeys.length" :show-icon="false" description="暂无可配置的状态项" />
          </div>
        </div>
        <template #actions>
          <NButton attr-type="button" :disabled="savingStatusSettings" @click="statusSettingsOpenModal.close()"> 取消 </NButton>
          <NButton
            :loading="savingStatusSettings"
            attr-type="button"
            :disabled="savingStatusSettings"
            @click="saveStatusSettings"
            type="primary"
          >
            {{ savingStatusSettings ? '正在保存' : '保存' }}
          </NButton>
        </template>
      </AppModal>
      <AppModal
        :show="featureSettingsOpenModal.show"
        @after-leave="featureSettingsOpenModal.afterLeave"
        v-if="featureSettingsOpen"
        :dismissible="!savingFeatureSettings"
        title="功能展示设置"
        @close="featureSettingsOpenModal.close()"
      >
        <div :inert="savingFeatureSettings">
          <NCheckbox v-model:checked="featureSettings.registerOnHome">注册到首页</NCheckbox>
          <NFormItem
            label="首页标题"
            :label-props="{ for: `${formId}-field-1` }"
            :show-feedback="false"
            class="form-field status-display-title"
          >
            <NInput
              :input-props="{ id: `${formId}-field-1` }"
              :value="featureSettings.homeTitle"
              @update:value="featureSettings.homeTitle = $event.trim()"
              maxlength="100"
              :placeholder="`${plugin.manifest?.name ?? plugin.id} 功能`"
            />
          </NFormItem>
          <div class="status-display-options">
            <strong>功能项</strong>
            <NCheckboxGroup
              v-model:value="selectedFeatureIds"
            ><NSpace vertical
              ><NCheckbox v-for="feature in features" :key="feature.id" :value="feature.id">{{
                  feature.name
                }}</NCheckbox></NSpace
              ></NCheckboxGroup
            >
            <NEmpty v-if="!features.length" :show-icon="false" description="暂无可配置的功能项" />
          </div>
        </div>
        <template #actions>
          <NButton attr-type="button" :disabled="savingFeatureSettings" @click="featureSettingsOpenModal.close()"> 取消 </NButton>
          <NButton
            :loading="savingFeatureSettings"
            attr-type="button"
            :disabled="savingFeatureSettings"
            @click="saveFeatureSettings"
            type="primary"
          >
            {{ savingFeatureSettings ? '正在保存' : '保存' }}
          </NButton>
        </template>
      </AppModal>
      <AppModal
        :show="settingsOpenModal.show"
        @after-leave="settingsOpenModal.afterLeave"
        v-if="settingsOpen"
        :dismissible="!savingSettings"
        title="配置展示设置"
        @close="settingsOpenModal.close()"
      >
        <NSpace vertical :size="16" class="plugin-configuration-display" :inert="savingSettings">
          <NCheckbox
            :checked="!settingsDisplay.registerOnHome"
            @update:checked="settingsDisplay.registerOnHome = !$event"
          >隐藏插件主页配置</NCheckbox
          >
          <div class="plugin-settings-fields">
            <template v-for="setting in settingsDefinitions" :key="setting.name">
              <PluginSettingField
                v-if="isPluginSetting(setting)"
                :field="setting"
                v-model="settingsConfig[setting.name]"
                :field-id="`info-setting-${setting.name}`"
              />
              <PluginSettingField v-else :field="setting" v-model="settingsConfig[setting.name]" />
            </template>
            <NEmpty :show-icon="false" v-if="!settingsDefinitions.length" class="plugin-empty">暂无配置项</NEmpty>
          </div>
        </NSpace>
        <template #actions>
          <NButton attr-type="button" :disabled="savingSettings" @click="settingsOpenModal.close()">取消</NButton>
          <NButton
            :loading="savingSettings"
            attr-type="button"
            :disabled="savingSettings"
            @click="saveSettings"
            type="primary"
          >
            {{ savingSettings ? '正在保存' : '保存' }}
          </NButton>
        </template>
      </AppModal>
      <AppModal
        :show="deleteOpenModal.show"
        @after-leave="deleteOpenModal.afterLeave"
        v-if="deleteOpen"
        title="删除插件"
        :dismissible="!deleting"
        :dismiss-on-backdrop="!deleting"
        @close="deleteOpenModal.close()"
      >
        <p>是否删除当前插件和配置文件</p>
        <template #actions>
          <NButton attr-type="button" :disabled="deleting" @click="deleteOpenModal.close()">
            取消
          </NButton>
          <NButton
            :loading="deleting && deletingData === false"
            attr-type="button"
            :disabled="deleting"
            @click="deletePlugin(false)"
            type="error"
          >
            {{ deleting && deletingData === false ? '正在删除' : '仅删除插件' }}
          </NButton>
          <NButton
            :loading="deleting && deletingData === true"
            attr-type="button"
            :disabled="deleting"
            @click="deletePlugin(true)"
            type="error"
          >
            {{ deleting && deletingData === true ? '正在删除' : '删除插件和配置' }}
          </NButton>
        </template>
      </AppModal>
    </template>
  </section>
</template>

<style>
.plugin-detail-hero { display: flex; align-items: center; justify-content: space-between; gap: 16px; }
.plugin-detail-hero h2, .plugin-detail-panel h3 { margin: 0; }
.plugin-detail-hero > div > p { margin: 6px 0 0; color: var(--text-muted); font-size: 14px; line-height: 1.55; }
.plugin-detail-id { color: var(--text-subtle) !important; font-size: 12px !important; font-weight: 400; }
.plugin-detail-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--page-card-row-gap) 18px; }
.plugin-info-list { display: grid; gap: 12px; margin: 18px 0 0; }
.plugin-info-list div { display: grid; grid-template-columns: 150px minmax(0, 1fr); gap: 12px; }
.plugin-info-list dt { color: var(--text-muted); font-size: 13px; }
.plugin-info-list dd { min-width: 0; margin: 0; color: var(--text); font-size: 13px; overflow-wrap: anywhere; }
.plugin-info-list a { color: var(--accent-text); text-decoration: none; }
@media (hover: hover) and (pointer: fine) {
  .plugin-info-list a:hover { text-decoration: underline; }
}
.path-value { color: var(--text-muted) !important; font-family: var(--font-mono); font-size: 12px !important; }
.feature-list { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 18px; }
@media (max-width: 640px) {
  .plugin-detail-hero { align-items: stretch; flex-direction: column; }
  .plugin-detail-grid { grid-template-columns: 1fr; }
  .plugin-info-list div { grid-template-columns: 1fr; gap: 4px; }
}
.plugin-authors { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.plugin-authors a { color: var(--text); text-decoration: none; }
@media (hover: hover) and (pointer: fine) {
  .plugin-authors a:hover { color: var(--accent-text); text-decoration: none; }
}
.plugin-detail-hero > div > p, .plugin-info-list dt, .plugin-info-list dd, .plugin-info-list a, .path-value, .plugin-authors a, .status-display-options > p { transition: color .3s cubic-bezier(.4, 0, .2, 1); }
.plugin-detail-view { max-width: none; }
.plugin-info-hero-actions { display: flex; align-items: center; gap: 10px; }
.status-display-options { display: grid; gap: 10px; margin-top: 18px; }
.status-display-options > strong { font-size: 14px; font-weight: 500; }
.status-display-options > p { margin: 0; color: var(--text-muted); font-size: 13px; }
.status-display-title { margin-top: 18px; }
.status-display-title input { width: 100%; min-width: 0; }
.plugin-detail-hero .plugin-info-hero-actions { flex: 0 0 auto; }
@media (max-width: 640px) {
  .plugin-detail-hero .plugin-info-hero-actions { align-self: flex-end; }
}
</style>

<style scoped>
.capability-arrival {
  outline-offset: 3px;
  animation: capability-arrival 1s ease-out;
}
@keyframes capability-arrival {
  0%, 70% { outline: 2px solid var(--accent-text); }
  100% { outline: 2px solid transparent; }
}
</style>
