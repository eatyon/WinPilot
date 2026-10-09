<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue';
import { createSettingsRequests } from '../SettingsRequests';
import { isAbortError, userErrorMessage } from '../../../api/client';
import AppIcon from '../../../components/AppIcon.vue';
import AppSelect from '../../../components/AppSelect.vue';
import AppMultiSelect from '../../../components/AppMultiSelect.vue';
import { showToast } from '../../../ui/toast';
import { safeModeEnabled } from '../../security/safeMode';

type DeliveryMode = 'AllEnabled' | 'SpecificChannels';
type NotificationMode = DeliveryMode | 'Disabled';
type SecurityNotificationSettings = {
  enabled: boolean;
  events: string[];
  deliveryMode: DeliveryMode;
  channels: string[];
};

const requests = createSettingsRequests();
const configured = ref(false);
const currentPassword = ref('');
const newPassword = ref('');
const confirmation = ref('');
const currentPasswordVisible = ref(false);
const newPasswordVisible = ref(false);
const confirmationVisible = ref(false);
const error = ref('');
const loading = ref(true);
const settingsLoaded = ref(false);
const saving = ref(false);
const notificationSaving = ref(false);
const notificationError = ref('');
const notifications = reactive<SecurityNotificationSettings>({
  enabled: false,
  events: [],
  deliveryMode: 'AllEnabled',
  channels: [],
});
const eventOptions = [
  ['PasswordSet', '管理员密码已设置/修改'],
  ['LanAccessChanged', '局域网访问已开启/关闭'],
  ['AuthConfigurationInvalid', '密码配置缺失或损坏'],
  ['LoginSourceLocked', '登录来源被锁定'],
  ['LoginSucceeded', '登录成功'],
  ['LoginFailed', '登录失败'],
  ['SessionsRevoked', 'Session 被撤销'],
  ['SecurityNotificationSettingsChanged', '安全通知配置已修改'],
] as const;
const channelOptions = [
  ['windows', 'Windows 通知'],
  ['webhook', 'Webhook'],
  ['telegram', 'Telegram'],
  ['workweixin', '企业微信'],
  ['feishu', '飞书'],
  ['onebot', 'OneBot'],
  ['qqbot', 'QQBot'],
] as const;

const mode = computed<NotificationMode>(() =>
  notifications.enabled ? notifications.deliveryMode : 'Disabled',
);
const modeOptions = [
  { value: 'Disabled', label: '关闭' },
  { value: 'AllEnabled', label: '所有已开启渠道' },
  { value: 'SpecificChannels', label: '指定渠道' },
];
function selectedLabel(values: string[], options: readonly (readonly [string, string])[], empty: string) {
  const labels = options.filter(([value]) => values.includes(value)).map(([, label]) => label);
  return labels.length ? labels.join('、') : empty;
}
const channelsLabel = computed(() => selectedLabel(notifications.channels, channelOptions, '请选择渠道'));
const eventsLabel = computed(() => {
  const count = eventOptions.filter(([value]) => eventSelected(value)).length;
  return count ? `${count} 个事件` : '请选择事件';
});
function eventSelected(value: string) {
  return value === 'PasswordSet'
    ? notifications.events.includes('PasswordSet') || notifications.events.includes('PasswordChanged')
    : notifications.events.includes(value);
}

onMounted(async () => {
  try {
    const [passwordResponse, notificationsResponse] = await Promise.all([
      requests.fetch('/api/security/password'),
      requests.fetch('/api/security/notifications'),
    ]);
    if (!passwordResponse.ok || !notificationsResponse.ok) throw new Error('无法读取安全设置');
    const [password, settings] = await Promise.all([
      requests.json<{ configured: boolean }>(passwordResponse),
      requests.json<SecurityNotificationSettings>(notificationsResponse),
    ]);
    configured.value = Boolean(password.configured);
    Object.assign(notifications, settings);
    settingsLoaded.value = true;
  } catch (cause) {
    if (!requests.active || isAbortError(cause)) return;
    error.value = userErrorMessage(cause, '无法读取安全设置');
  } finally {
    loading.value = false;
  }
});

onBeforeUnmount(() => requests.dispose());

function toggle(values: string[], value: string, checked: boolean) {
  const index = values.indexOf(value);
  if (checked && index < 0) values.push(value);
  if (!checked && index >= 0) values.splice(index, 1);
}
function toggleEvent(value: string, checked: boolean) {
  toggle(notifications.events, value, checked);
  if (value === 'PasswordSet') toggle(notifications.events, 'PasswordChanged', checked);
}
function selectMode(mode: NotificationMode) {
  if (mode === 'Disabled') notifications.enabled = false;
  else {
    notifications.enabled = true;
    notifications.deliveryMode = mode;
  }
}
async function savePassword() {
  if (safeModeEnabled.value || !settingsLoaded.value || saving.value || !requests.active) return;
  error.value = '';
  saving.value = true;
  try {
    const response = await requests.fetch('/api/security/password', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        currentPassword: currentPassword.value,
        newPassword: newPassword.value,
        confirmation: confirmation.value,
      }),
    });
    if (!response.ok) {
      const problem = (await requests.json(response).catch(() => undefined)) as
        { errors?: Record<string, string[]> } | undefined;
      throw new Error(problem?.errors?.password?.[0] ?? '无法修改密码');
    }
    requests.ensureActive();
    currentPassword.value = '';
    newPassword.value = '';
    confirmation.value = '';
    window.location.assign('/login');
  } catch (cause) {
    if (!requests.active || isAbortError(cause)) return;
    error.value = userErrorMessage(cause, '无法修改密码');
  } finally {
    saving.value = false;
  }
}
async function saveNotifications() {
  if (safeModeEnabled.value || !settingsLoaded.value || notificationSaving.value || !requests.active) return;
  notificationError.value = '';
  if (
    notifications.enabled &&
    notifications.deliveryMode === 'SpecificChannels' &&
    notifications.channels.length === 0
  ) {
    notificationError.value = '指定渠道时至少选择一个通知渠道';
    return;
  }
  notificationSaving.value = true;
  try {
    const response = await requests.fetch('/api/security/notifications', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(notifications),
    });
    const result = (await requests.json(response).catch(() => undefined)) as
      SecurityNotificationSettings | { errors?: Record<string, string[]> } | undefined;
    requests.ensureActive();
    if (!response.ok) {
      const problem = result as { errors?: Record<string, string[]> } | undefined;
      throw new Error(problem?.errors?.notifications?.[0] ?? '无法保存安全通知设置');
    }
    Object.assign(notifications, result as SecurityNotificationSettings);
    showToast('安全事件通知设置已保存', 'success');
  } catch (cause) {
    if (!requests.active || isAbortError(cause)) return;
    notificationError.value = userErrorMessage(cause, '无法保存安全通知设置');
  } finally {
    notificationSaving.value = false;
  }
}
</script>

<template>
  <div class="settings-tab-content security-settings" :aria-busy="loading">
    <p v-if="!settingsLoaded && error" class="status-message error" role="alert">{{ error }}</p>
    <section class="settings-section">
      <div class="settings-section-heading">
        <h3>管理员密码</h3>
        <p>密码用于 Web 管理面的身份认证</p>
      </div>
      <div v-if="settingsLoaded" class="security-password-form">
        <label v-if="configured" class="field-control">
          <span>当前密码</span>
          <span class="secret-input">
            <input
              v-model="currentPassword"
              :type="currentPasswordVisible ? 'text' : 'password'"
              autocomplete="current-password"
            />
            <button
              type="button"
              class="secret-toggle"
              :aria-label="currentPasswordVisible ? '隐藏当前密码' : '显示当前密码'"
              :title="currentPasswordVisible ? '隐藏当前密码' : '显示当前密码'"
              @click="currentPasswordVisible = !currentPasswordVisible"
            >
              <AppIcon :name="currentPasswordVisible ? 'eye-off' : 'eye'" />
            </button>
          </span>
        </label>
        <label class="field-control">
          <span>{{ configured ? '新密码' : '管理员密码' }}</span>
          <span class="secret-input">
            <input
              v-model="newPassword"
              :type="newPasswordVisible ? 'text' : 'password'"
              autocomplete="new-password"
            />
            <button
              type="button"
              class="secret-toggle"
              :aria-label="newPasswordVisible ? '隐藏密码' : '显示密码'"
              :title="newPasswordVisible ? '隐藏密码' : '显示密码'"
              @click="newPasswordVisible = !newPasswordVisible"
            >
              <AppIcon :name="newPasswordVisible ? 'eye-off' : 'eye'" />
            </button>
          </span>
        </label>
        <label class="field-control">
          <span>{{ configured ? '确认新密码' : '确认密码' }}</span>
          <span class="secret-input">
            <input
              v-model="confirmation"
              :type="confirmationVisible ? 'text' : 'password'"
              autocomplete="new-password"
            />
            <button
              type="button"
              class="secret-toggle"
              :aria-label="confirmationVisible ? '隐藏确认密码' : '显示确认密码'"
              :title="confirmationVisible ? '隐藏确认密码' : '显示确认密码'"
              @click="confirmationVisible = !confirmationVisible"
            >
              <AppIcon :name="confirmationVisible ? 'eye-off' : 'eye'" />
            </button>
          </span>
        </label>
        <p v-if="error" class="status-message error" role="alert">{{ error }}</p>
        <button class="button button-primary" type="button" :disabled="saving" @click="savePassword">
          {{ saving ? '正在保存' : configured ? '修改密码' : '设置密码' }}
        </button>
      </div>
    </section>

    <section class="settings-section security-notification-settings">
      <div class="settings-section-heading">
        <h3>安全事件通知</h3>
        <p>发生安全事件发送通知提醒</p>
      </div>
      <div class="security-notification-form">
        <label class="field-control">
          <span>通知模式</span>
          <AppSelect
            :model-value="mode"
            :options="modeOptions"
            :disabled="loading || !settingsLoaded || notificationSaving"
            @change="selectMode"
          />
        </label>
        <label
          v-if="notifications.enabled && notifications.deliveryMode === 'SpecificChannels'"
          class="field-control"
        >
          <span>通知渠道</span>
          <AppMultiSelect
            :model-value="notifications.channels"
            :options="channelOptions.map(([value, label]) => ({ value, label }))"
            :label="channelsLabel"
            :disabled="
              loading || !settingsLoaded ||
              notificationSaving ||
              !notifications.enabled ||
              notifications.deliveryMode !== 'SpecificChannels'
            "
            aria-label="通知渠道"
            @toggle="(value, checked) => toggle(notifications.channels, value, checked)"
          />
        </label>
        <label class="field-control security-notification-events">
          <span>通知事件</span>
          <AppMultiSelect
            :model-value="eventOptions.filter(([value]) => eventSelected(value)).map(([value]) => value)"
            :options="eventOptions.map(([value, label]) => ({ value, label }))"
            :label="eventsLabel"
            :disabled="loading || !settingsLoaded || notificationSaving"
            :max-height="320"
            aria-label="通知事件"
            @toggle="toggleEvent"
          />
        </label>
        <p v-if="notificationError" class="status-message error security-notification-error" role="alert">
          {{ notificationError }}
        </p>
        <div class="settings-actions security-notification-actions">
          <button
            class="button button-primary"
            type="button"
            :disabled="loading || !settingsLoaded || notificationSaving"
            @click="saveNotifications"
          >
            {{ notificationSaving ? '正在保存' : '保存设置' }}
          </button>
        </div>
      </div>
    </section>
  </div>
</template>
