<script setup lang="ts">
import { computed, h, onBeforeUnmount, onMounted, ref } from 'vue';
import { NButton, NDropdown, NText, useThemeVars, type DropdownOption } from 'naive-ui';
import AppIcon from '../../components/AppIcon.vue';
import { apiFetch, isAbortError, readApiError, redirectToLogin, userErrorMessage } from '../../api/client';
import { showToast } from '../../ui/toast';
import { useDelayedLoading } from '../../ui/useDelayedLoading';
import { safeModeEnabled } from '../security/safeMode';

const shown = ref(false);
const passwordConfigured = ref<boolean>();
const loadingPasswordState = ref(false);
const loggingOut = ref(false);
const showLogoutLoading = useDelayedLoading(() => loggingOut.value);
const controller = new AbortController();
const theme = useThemeVars();
const logoutDisabled = computed(() => passwordConfigured.value !== true || loadingPasswordState.value || loggingOut.value);
const menuTheme = computed(() => logoutDisabled.value ? undefined : {
  optionTextColor: theme.value.errorColor,
  optionTextColorHover: theme.value.errorColor,
  optionColorHover: `color-mix(in srgb, ${theme.value.errorColor} 12%, transparent)`,
});
const options = computed<DropdownOption[]>(() => [
  ...(safeModeEnabled.value ? [
    {
      key: 'safe-mode',
      type: 'render' as const,
      render: () => h(NText, {
        tag: 'div',
        depth: 3,
        role: 'status',
        style: { padding: '8px 12px', maxWidth: 'min(280px, calc(100vw - 48px))', whiteSpace: 'normal' },
      }, () => '安全模式已开启，部分功能不可用'),
    },
    { key: 'safe-mode-divider', type: 'divider' as const },
  ] : []),
  {
    key: 'logout',
    label: '退出登录',
    disabled: logoutDisabled.value,
  },
]);

onMounted(loadPasswordState);
onBeforeUnmount(() => controller.abort());

async function loadPasswordState() {
  if (loadingPasswordState.value || loggingOut.value || controller.signal.aborted) return;
  loadingPasswordState.value = true;
  passwordConfigured.value = undefined;
  try {
    const response = await apiFetch('/api/settings/security/password', { cache: 'no-store', signal: controller.signal });
    if (!response.ok) throw new Error(await readApiError(response, '无法读取密码设置'));
    const state = await response.json() as { configured: boolean };
    if (typeof state.configured !== 'boolean') throw new Error('无法读取密码设置');
    if (!controller.signal.aborted) passwordConfigured.value = state.configured;
  } catch (error) {
    if (!controller.signal.aborted && !isAbortError(error)) showToast(userErrorMessage(error, '无法读取密码设置'));
  } finally {
    loadingPasswordState.value = false;
  }
}

function updateShow(value: boolean) {
  shown.value = value;
  if (value && passwordConfigured.value === undefined) void loadPasswordState();
}

async function select(key: string) {
  if (key !== 'logout' || logoutDisabled.value || controller.signal.aborted) return;
  loggingOut.value = true;
  try {
    const response = await apiFetch('/api/logout', { method: 'POST', signal: controller.signal });
    if (!response.ok && response.status !== 401) throw new Error(await readApiError(response, '退出登录失败'));
    if (!controller.signal.aborted) redirectToLogin();
  } catch (error) {
    if (!controller.signal.aborted && !isAbortError(error)) showToast(userErrorMessage(error, '退出登录失败'));
  } finally {
    loggingOut.value = false;
  }
}
</script>

<template>
  <NDropdown trigger="click" placement="bottom-end" :show="shown" :options="options" :theme-overrides="menuTheme" @update:show="updateShow" @select="select">
    <NButton
      quaternary
      class="app-icon-button"
      aria-label="用户菜单"
      aria-haspopup="menu"
      :aria-expanded="shown"
      :aria-busy="loggingOut"
      :loading="showLogoutLoading"
      :disabled="loggingOut"
    >
      <template #icon><AppIcon :name="safeModeEnabled ? 'lock' : 'account'" /></template>
    </NButton>
  </NDropdown>
</template>
