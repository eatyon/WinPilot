<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { createSettingsRequests } from '../SettingsRequests';
import { isAbortError, WinPilotConnectionError } from '../../../api/client';
import AppIcon from '../../../components/AppIcon.vue';
import AppModal from '../../../components/AppModal.vue';
import AppSelect from '../../../components/AppSelect.vue';
import { safeModeEnabled, safeModeMessage } from '../../security/safeMode';
import { showToast } from '../../../ui/toast';
import { renderMarkdown } from '../../../ui/markdown';

const requests = createSettingsRequests();
const repositoryUrl = 'https://github.com/eatyon/WinPilot';
const releaseUrl = `${repositoryUrl}/releases`;
const issueUrl = `${repositoryUrl}/issues`;
const noticesUrl = `${repositoryUrl}/blob/main/THIRD_PARTY_NOTICES.md`;
const version = ref('');
const checkingUpdate = ref(false);
const installingUpdate = ref(false);
const updateMessage = ref('');
const updateMessageType = ref<'neutral' | 'success' | 'error'>('neutral');
const updateReleaseUrl = ref('');
const canInstallUpdate = ref(false);
const updateSettingsOpen = ref(false);
const updateVersion = ref('');
const updateNotes = ref('');
const updateNotesOpen = ref(false);
const updateNotesLoading = ref(false);
const updateNotesHtml = ref('');
let updateNotesGeneration = 0;
type UpdateSource = 'GitHub' | 'Cnb';
type UpdateChannel = 'Stable' | 'Preview';
type UpdateSettings = { source: UpdateSource; channel: UpdateChannel };
const draftSource = ref<UpdateSource>('GitHub');
const draftChannel = ref<UpdateChannel>('Stable');
const loadingUpdateSettings = ref(false);
const savingUpdateSettings = ref(false);
const updateSources = [{ value: 'GitHub', label: 'GitHub' }, { value: 'Cnb', label: 'CNB' }];
const updateChannels = [{ value: 'Stable', label: '正式版' }, { value: 'Preview', label: '测试版' }];
let updateStatusTimer: number | undefined;
let updateReconnectDeadline = 0;

async function openUpdateSettings() {
  if (!requests.active || safeModeEnabled.value || loadingUpdateSettings.value || checkingUpdate.value || installingUpdate.value) return;
  loadingUpdateSettings.value = true;
  try {
    const response = await requests.fetch('/api/update/settings');
    if (!response.ok) throw new Error();
    const settings = await requests.json(response) as UpdateSettings;
    draftSource.value = settings.source;
    draftChannel.value = settings.channel;
    if (!safeModeEnabled.value) updateSettingsOpen.value = true;
  } catch (cause) {
    if (!requests.active || isAbortError(cause)) return;
    if (!(cause instanceof WinPilotConnectionError)) showToast('无法读取更新设置，请稍后重试');
  } finally { loadingUpdateSettings.value = false; }
}

async function saveUpdateSettings() {
  if (!requests.active || safeModeEnabled.value || savingUpdateSettings.value || checkingUpdate.value || installingUpdate.value) return;
  savingUpdateSettings.value = true;
  try {
    const response = await requests.fetch('/api/update/settings', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ source: draftSource.value, channel: draftChannel.value }),
    });
    if (!response.ok) {
      const error = await requests.json(response).catch(() => ({})) as { message?: string; detail?: string };
      throw new Error(error.message ?? error.detail ?? '保存更新设置失败，请稍后重试');
    }
    const settings = await requests.json(response) as UpdateSettings;
    draftSource.value = settings.source;
    draftChannel.value = settings.channel;
    resetUpdateResult();
    updateSettingsOpen.value = false;
    showToast('更新设置已保存', 'success');
  } catch (cause) {
    if (!requests.active || isAbortError(cause)) return;
    if (!(cause instanceof WinPilotConnectionError)) showToast(cause instanceof Error ? cause.message : '保存更新设置失败，请稍后重试');
  } finally { savingUpdateSettings.value = false; }
}

function closeUpdateSettings() {
  if (!savingUpdateSettings.value) updateSettingsOpen.value = false;
}

function closeUpdateNotes() {
  updateNotesGeneration++;
  updateNotesOpen.value = false;
  updateNotesLoading.value = false;
  updateNotesHtml.value = '';
}

function resetUpdateResult() {
  closeUpdateNotes();
  updateMessage.value = '';
  updateMessageType.value = 'neutral';
  canInstallUpdate.value = false;
  updateReleaseUrl.value = '';
  updateVersion.value = '';
  updateNotes.value = '';
}

async function openUpdateNotes() {
  if (!requests.active || !updateReleaseUrl.value || updateNotesOpen.value) return;
  updateNotesOpen.value = true;
  if (!updateNotes.value.trim()) return;
  const generation = ++updateNotesGeneration;
  updateNotesLoading.value = true;
  try {
    const html = await renderMarkdown(updateNotes.value);
    if (generation === updateNotesGeneration && requests.active) updateNotesHtml.value = html;
  } catch {
    if (generation === updateNotesGeneration && requests.active) {
      closeUpdateNotes();
      showToast('无法显示更新说明，请稍后重试');
    }
  } finally {
    if (generation === updateNotesGeneration) updateNotesLoading.value = false;
  }
}

watch(safeModeEnabled, (enabled) => {
  if (enabled) updateSettingsOpen.value = false;
});

onMounted(async () => {
  try {
    const response = await requests.fetch('/api/about');
    if (!response.ok) return;
    const result = (await requests.json(response)) as { version?: string };
    version.value = result.version ?? '';
  } catch {}
});

async function checkForUpdates() {
  if (!requests.active || checkingUpdate.value || installingUpdate.value || savingUpdateSettings.value) return;
  checkingUpdate.value = true;
  resetUpdateResult();
  try {
    const response = await requests.fetch('/api/update/check', { method: 'POST' });
    if (!response.ok) throw new Error();
    const result = (await requests.json(response)) as {
      latestVersion?: string;
      updateAvailable?: boolean;
      releaseUrl?: string;
      releaseNotes?: string;
      canInstall?: boolean;
      checkFailed?: boolean;
      source: UpdateSource;
      channel: UpdateChannel;
    };
    if (result.checkFailed) {
      const sourceName = updateSources.find(source => source.value === result.source)?.label ?? '所选更新源';
      updateMessage.value = `无法通过 ${sourceName} 检查更新，请稍后重试`;
      updateMessageType.value = 'error';
    }
    else if (result.updateAvailable && result.latestVersion) {
      updateReleaseUrl.value = result.releaseUrl ?? releaseUrl;
      updateVersion.value = result.latestVersion;
      updateNotes.value = result.releaseNotes ?? '';
      canInstallUpdate.value = result.canInstall === true;
      updateMessage.value = `发现新版本 v${result.latestVersion}${canInstallUpdate.value ? '' : '，此版本暂不支持在线安装'}`;
      updateMessageType.value = canInstallUpdate.value ? 'success' : 'neutral';
    } else {
      updateMessage.value = '当前已是最新版本';
      updateMessageType.value = 'success';
    }
  } catch (cause) {
    if (!requests.active || isAbortError(cause)) return;
    updateMessage.value = cause instanceof WinPilotConnectionError ? '' : '暂时无法检查更新，请稍后重试';
    updateMessageType.value = 'error';
  } finally {
    checkingUpdate.value = false;
  }
}

async function installUpdate() {
  if (!requests.active || installingUpdate.value || checkingUpdate.value || savingUpdateSettings.value || !canInstallUpdate.value) return;
  installingUpdate.value = true;
  updateReconnectDeadline = 0;
  updateMessageType.value = 'neutral';
  try {
    const response = await requests.fetch('/api/update/install', { method: 'POST' });
    if (!response.ok) throw new Error();
    updateMessage.value = '正在准备更新…';
    await loadUpdateStatus();
  } catch (cause) {
    if (!requests.active || isAbortError(cause)) return;
    if (cause instanceof WinPilotConnectionError) {
      await loadUpdateStatus();
      return;
    }
    finishUpdate('无法启动在线更新，请前往版本发布页面手动下载', 'error');
  }
}

function finishUpdate(message: string, tone: 'neutral' | 'success' | 'error') {
  updateMessage.value = message;
  updateMessageType.value = tone;
  installingUpdate.value = false;
  canInstallUpdate.value = false;
  updateReconnectDeadline = 0;
  window.clearTimeout(updateStatusTimer);
  updateStatusTimer = undefined;
}

async function loadUpdateStatus() {
  try {
    const response = await requests.fetch('/api/update/status');
    if (!response.ok) throw new Error();
    const status = (await requests.json(response)) as {
      state: string;
      downloadedBytes: number;
      totalBytes?: number;
      message: string;
    };
    if (status.state === 'Idle') {
      const aboutResponse = await requests.fetch('/api/about');
      if (!aboutResponse.ok) throw new Error();
      const about = await requests.json(aboutResponse) as { version?: string };
      if (!about.version) finishUpdate('无法确认更新结果', 'neutral');
      else {
        const success = about.version.split('+')[0] === updateVersion.value;
        if (success) version.value = about.version;
        finishUpdate(success ? '更新成功' : '更新失败', success ? 'success' : 'error');
      }
      return;
    }
    if (status.state === 'Restarting') {
      if (!updateReconnectDeadline) updateReconnectDeadline = Date.now() + 60_000;
      if (Date.now() >= updateReconnectDeadline) {
        finishUpdate('无法确认更新结果', 'neutral');
        return;
      }
    } else updateReconnectDeadline = 0;
    if (status.state === 'Failed') {
      finishUpdate(status.message || '更新失败', 'error');
      return;
    }
    const percentage =
      status.totalBytes && status.totalBytes > 0
        ? ` ${Math.min(100, Math.round((status.downloadedBytes * 100) / status.totalBytes))}%`
        : '';
    updateMessage.value = `${status.message}${status.state === 'Downloading' ? percentage : ''}`;
    updateMessageType.value = 'neutral';
    updateStatusTimer = window.setTimeout(() => void loadUpdateStatus(), 400);
  } catch (cause) {
    if (!requests.active || isAbortError(cause)) return;
    if (cause instanceof WinPilotConnectionError) {
      if (!updateReconnectDeadline) updateReconnectDeadline = Date.now() + 60_000;
      if (Date.now() < updateReconnectDeadline) {
        updateStatusTimer = window.setTimeout(() => void loadUpdateStatus(), 1000);
        return;
      }
    }
    finishUpdate('无法确认更新结果', 'neutral');
  }
}

onBeforeUnmount(() => { closeUpdateNotes(); requests.dispose(); window.clearTimeout(updateStatusTimer); });
</script>

<template>
  <div class="settings-tab-content about-settings">
    <section class="settings-section about-product-section">
      <div class="about-product-mark" aria-hidden="true">
        <img class="about-product-foreground" src="/favicon-foreground-mask.png" alt="" draggable="false" />
      </div>
      <div class="about-product-copy">
        <p class="section-kicker">本机控制与自动化</p>
        <h3>WinPilot</h3>
        <p>
          当前版本
          <strong>v{{ version || '—' }}</strong>
        </p>
      </div>
    </section>

    <section class="settings-section">
      <div class="about-update-heading">
        <div class="settings-section-heading">
          <h3>软件更新</h3>
          <p>检查新版本，获取功能改进和问题修复说明</p>
        </div>
        <button
          class="button button-secondary about-update-settings-button"
          type="button"
          :disabled="safeModeEnabled || loadingUpdateSettings || installingUpdate"
          :title="safeModeEnabled ? safeModeMessage : installingUpdate ? '更新期间无法修改设置' : '更新设置'"
          aria-label="更新设置"
          @click="openUpdateSettings"
        >
          <AppIcon name="settings" />
        </button>
      </div>
      <div class="about-update-actions">
        <button
          class="button button-primary"
          type="button"
          :disabled="checkingUpdate || savingUpdateSettings || installingUpdate"
          @click="checkForUpdates"
        >
          {{ checkingUpdate ? '正在检查…' : '检查更新' }}
        </button>
        <button
          v-if="canInstallUpdate"
          class="button button-primary"
          type="button"
          :disabled="installingUpdate || savingUpdateSettings"
          @click="installUpdate"
        >
          {{ installingUpdate ? '正在下载…' : '下载并安装' }}
        </button>
      </div>
      <p
        v-if="updateMessage"
        class="about-update-status"
        :class="updateMessageType"
        role="status"
      >
        {{ updateMessage }}
        <button v-if="updateReleaseUrl" class="about-update-notes-link" type="button" @click="openUpdateNotes">查看更新说明</button>
      </p>
    </section>

    <section class="settings-section">
      <div class="settings-section-heading">
        <h3>项目与支持</h3>
        <p>查看项目资料、反馈问题或获取已发布版本</p>
      </div>
      <div class="about-link-list">
        <a :href="repositoryUrl" target="_blank" rel="noopener noreferrer">
          <span>项目主页</span>
          <span>GitHub</span>
        </a>
        <a :href="issueUrl" target="_blank" rel="noopener noreferrer">
          <span>问题反馈</span>
          <span>提交 Issue</span>
        </a>
        <a :href="releaseUrl" target="_blank" rel="noopener noreferrer">
          <span>版本发布</span>
          <span>查看 Releases</span>
        </a>
      </div>
    </section>

    <section class="settings-section">
      <div class="settings-section-heading">
        <h3>开源许可</h3>
        <p>WinPilot 采用 AGPL-3.0-only 许可证发布；第三方组件及其许可证声明可在仓库中查看</p>
      </div>
      <div class="about-link-list">
        <a :href="`${repositoryUrl}/blob/main/LICENSE`" target="_blank" rel="noopener noreferrer">
          <span>软件许可证</span>
          <span>AGPL-3.0-only</span>
        </a>
        <a :href="noticesUrl" target="_blank" rel="noopener noreferrer">
          <span>第三方组件声明</span>
          <span>查看声明</span>
        </a>
      </div>
    </section>
    <AppModal v-if="updateNotesOpen" :title="`更新说明 v${updateVersion}`" size="medium" @close="closeUpdateNotes">
      <p v-if="updateNotesLoading">正在加载…</p>
      <div v-else-if="updateNotesHtml" class="markdown-content" v-html="updateNotesHtml"></div>
      <p v-else>此版本暂无发布说明</p>
      <template #actions>
        <button class="button button-secondary" type="button" @click="closeUpdateNotes">关闭</button>
        <a class="button button-primary" :href="updateReleaseUrl" target="_blank" rel="noopener noreferrer">前往发布页面</a>
      </template>
    </AppModal>
    <AppModal v-if="updateSettingsOpen" title="更新设置" @close="closeUpdateSettings">
      <div class="about-update-settings-fields">
        <label class="about-update-settings-field">
          <span>更新源</span>
          <AppSelect v-model="draftSource" :options="updateSources" :disabled="savingUpdateSettings" aria-label="更新源" />
        </label>
        <label class="about-update-settings-field">
          <span>版本通道</span>
          <AppSelect v-model="draftChannel" :options="updateChannels" :disabled="savingUpdateSettings" aria-label="版本通道" />
        </label>
      </div>
      <template #actions>
        <button class="button button-secondary" type="button" :disabled="savingUpdateSettings" @click="closeUpdateSettings">取消</button>
        <button class="button button-primary" type="button" :disabled="savingUpdateSettings || checkingUpdate || installingUpdate" @click="saveUpdateSettings">{{ savingUpdateSettings ? '正在保存…' : '保存' }}</button>
      </template>
    </AppModal>
  </div>
</template>

<style scoped>
.about-update-notes-link {
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--accent-text);
  font: inherit;
  cursor: pointer;
}
.about-update-notes-link:hover { text-decoration: underline; }
.about-update-status {
  color: var(--text-muted);
}
.about-update-status.success {
  color: var(--success-text);
}
.about-update-status.error {
  color: var(--danger-text);
}
.about-update-heading {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}
.about-update-settings-button {
  flex: 0 0 auto;
  padding-inline: 9px;
  border-color: transparent;
  background: transparent;
  color: var(--text-muted);
}
.about-update-settings-button:hover:not(:disabled) {
  border-color: transparent;
  background: var(--surface-hover);
  color: var(--accent-text);
}
.about-update-settings-button:disabled {
  color: var(--text-subtle);
  opacity: .72;
}
.about-update-settings-fields {
  display: grid;
  gap: 20px;
}
.about-update-settings-field {
  display: grid;
  gap: 8px;
  font-size: 14px;
}
</style>
