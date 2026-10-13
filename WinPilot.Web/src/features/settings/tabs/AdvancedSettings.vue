<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import { createSettingsRequests } from '../SettingsRequests';
import { readApiError, isAbortError, userErrorMessage } from '../../../api/client';
import { showToast } from '../../../ui/toast';
import { safeModeEnabled } from '../../security/safeMode';

const requests = createSettingsRequests();
const enabled = ref(false);
const loading = ref(true);
const saving = ref(false);
const settingsLoaded = ref(false);
const error = ref('');

onMounted(async () => {
  try {
    const response = await requests.fetch('/api/settings/advanced/unauthenticated-web-page');
    if (!response.ok) throw new Error(await readApiError(response, '无法读取设置', requests.json));
    enabled.value = ((await requests.json(response)) as { enabled: boolean }).enabled;
    settingsLoaded.value = true;
  } catch (cause) {
    if (!requests.active || isAbortError(cause)) return;
    error.value = userErrorMessage(cause, '无法读取设置');
  } finally {
    loading.value = false;
  }
});

onBeforeUnmount(() => requests.dispose());

async function save(next: boolean) {
  if (safeModeEnabled.value || !settingsLoaded.value || saving.value || !requests.active) return;
  const previous = enabled.value;
  enabled.value = next;
  saving.value = true;
  error.value = '';
  try {
    const response = await requests.fetch('/api/settings/advanced/unauthenticated-web-page', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled: next }),
    });
    if (!response.ok) throw new Error(await readApiError(response, '无法保存设置', requests.json));
    enabled.value = ((await requests.json(response)) as { enabled: boolean }).enabled;
    showToast(next ? 'Web 页面伪装已开启' : 'Web 页面伪装已关闭', 'success');
  } catch (cause) {
    if (!requests.active || isAbortError(cause)) return;
    enabled.value = previous;
    error.value = userErrorMessage(cause, '无法保存设置');
  } finally {
    saving.value = false;
  }
}
</script>

<template>
  <div class="settings-tab-content advanced-settings">
    <section class="settings-section">
      <div class="settings-section-heading">
        <h3>Web 页面伪装</h3>
        <p>
          开启后，未登录时将显示
          <code>User/Web/index.html</code>
          自定义页面
        </p>
      </div>
      <label class="toggle-control settings-toggle" :class="{ 'settings-data-pending': !settingsLoaded }">
        <input
          :checked="enabled"
          type="checkbox"
          :disabled="loading || !settingsLoaded || saving"
          @change="save(($event.target as HTMLInputElement).checked)"
        />
        <span>开启 Web 页面伪装</span>
      </label>
      <p class="settings-help">首次开启会创建默认页面文件，长按页面进入登录页</p>
      <p v-if="error" class="status-message error" role="alert">{{ error }}</p>
    </section>
  </div>
</template>
