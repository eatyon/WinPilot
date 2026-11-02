<script setup lang="ts">
import AppLoadError from '../../../components/AppLoadError.vue';
import AppPasswordInput from '../../../components/AppPasswordInput.vue';
import { NFormItem, NButton, NCard, NSkeleton } from 'naive-ui';
import { useId, computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import { createSettingsRequests } from '../settingsRequests';
import { readApiError, isAbortError, userErrorMessage } from '../../../api/client';
import AppSelect from '../../../components/AppSelect.vue';
import AppMultiSelect from '../../../components/AppMultiSelect.vue';
import { showToast } from '../../../ui/toast';
import { safeModeEnabled } from '../../security/safeMode';

const formId = useId();

type SecurityNotificationSettings = {
  channel: string;
  events: string[];
};

const requests = createSettingsRequests();
const configured = ref(false);
const currentPassword = ref('');
const newPassword = ref('');
const confirmation = ref('');
const error = ref('');
const loading = ref(true);
const settingsLoaded = ref(false);
const initialLoading = computed(() => loading.value && !settingsLoaded.value && !error.value);
const saving = ref(false);
const notificationSaving = ref(false);
type PasswordField = 'currentPassword' | 'newPassword' | 'confirmation';
const passwordFeedback = ref<{ field: PasswordField; message: string }>();
let passwordFormVersion = 0;
watch([currentPassword, newPassword, confirmation], () => {
  passwordFormVersion++;
  passwordFeedback.value = undefined;
  if (settingsLoaded.value) error.value = '';
}, { flush: 'sync' });
function passwordFieldError(field: PasswordField) {
  return passwordFeedback.value?.field === field ? passwordFeedback.value.message : undefined;
}
function passwordValidationField(): PasswordField | undefined {
  if (newPassword.value.length < 6 || newPassword.value !== newPassword.value.trim()) return 'newPassword';
  if (newPassword.value !== confirmation.value) return 'confirmation';
  return configured.value ? 'currentPassword' : undefined;
}
const notifications = reactive<SecurityNotificationSettings>({
  channel: '',
  events: [],
});
const eventOptions = [
  ['PasswordSet', '管理员密码已设置/修改'],
  ['LanAccessChanged', '局域网访问已开启/关闭'],
  ['AuthConfigurationInvalid', '密码配置缺失或损坏'],
  ['LoginSourceLocked', '登录来源已锁定'],
  ['LoginSucceeded', '登录成功'],
  ['LoginFailed', '登录失败'],
  ['SecurityNotificationSettingsChanged', '安全事件通知配置已修改'],
] as const;
const eventSelectOptions = eventOptions.map(([value, label]) => ({ value, label }));
const channelOptions = ref<{ value: string; label: string }[]>([]);
const selectedEvents = computed(() =>
  eventOptions.filter(([value]) => eventSelected(value)).map(([value]) => value),
);
const eventsLabel = computed(() => {
  const count = selectedEvents.value.length;
  return count ? `${count} 个事件` : '请选择事件';
});
function eventSelected(value: string) {
  return value === 'PasswordSet'
    ? notifications.events.includes('PasswordSet') ||
        notifications.events.includes('PasswordChanged')
    : notifications.events.includes(value);
}

async function load() {
  loading.value = true;
  try {
    const [passwordResponse, notificationsResponse, channelsResponse] = await Promise.all([
      requests.fetch('/api/settings/security/password'),
      requests.fetch('/api/settings/security/notifications'),
      requests.fetch('/api/notifications/channels'),
    ]);
    if (!passwordResponse.ok || !notificationsResponse.ok || !channelsResponse.ok)
      throw new Error('无法读取安全设置');
    const [password, settings, channels] = await Promise.all([
      requests.json<{ configured: boolean }>(passwordResponse),
      requests.json<SecurityNotificationSettings>(notificationsResponse),
      requests.json<{ value: string; label: string }[]>(channelsResponse),
    ]);
    configured.value = Boolean(password.configured);
    Object.assign(notifications, settings);
    channelOptions.value = [
      { value: '', label: '禁用' },
      ...channels.map((option) => ({
        value: option.value.toLowerCase(),
        label: option.label,
      })),
    ];
    settingsLoaded.value = true;
    error.value = '';
  } catch (cause) {
    if (!requests.active || isAbortError(cause)) return;
    error.value = userErrorMessage(cause, '无法读取安全设置');
  } finally {
    loading.value = false;
  }
}
onMounted(load);
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
async function savePassword() {
  if (safeModeEnabled.value || !settingsLoaded.value || saving.value || !requests.active) return;
  error.value = '';
  passwordFeedback.value = undefined;
  const submittedVersion = passwordFormVersion;
  saving.value = true;
  try {
    const response = await requests.fetch('/api/settings/security/password', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        currentPassword: currentPassword.value,
        newPassword: newPassword.value,
        confirmation: confirmation.value,
      }),
    });
    if (!response.ok) {
      let passwordIssue = false;
      const message = await readApiError(response, '无法修改密码', async response => {
        const problem = await requests.json<{ errors?: Record<string, unknown> } | null>(response);
        const messages = problem?.errors?.password;
        passwordIssue = response.status === 400 && Array.isArray(messages) && messages.some(value => typeof value === 'string' && value.trim());
        return problem;
      });
      requests.ensureActive();
      if (submittedVersion !== passwordFormVersion) return;
      if (passwordIssue) {
        const field = passwordValidationField();
        if (field) {
          passwordFeedback.value = { field, message };
          return;
        }
      }
      throw new Error(message);
    }
    requests.ensureActive();
    currentPassword.value = '';
    newPassword.value = '';
    confirmation.value = '';
    window.location.assign('/login');
  } catch (cause) {
    if (!requests.active || isAbortError(cause) || submittedVersion !== passwordFormVersion) return;
    showToast(userErrorMessage(cause, '无法修改密码'));
  } finally {
    saving.value = false;
  }
}
async function saveNotifications() {
  if (
    safeModeEnabled.value ||
    !settingsLoaded.value ||
    notificationSaving.value ||
    !requests.active
  )
    return;
  notificationSaving.value = true;
  try {
    const response = await requests.fetch('/api/settings/security/notifications', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(notifications),
    });
    if (!response.ok)
      throw new Error(await readApiError(response, '无法保存安全通知设置', requests.json));
    const result = await requests.json<SecurityNotificationSettings>(response);
    Object.assign(notifications, result);
    showToast('安全事件通知设置已保存', 'success');
  } catch (cause) {
    if (!requests.active || isAbortError(cause)) return;
    showToast(userErrorMessage(cause, '无法保存安全通知设置'));
  } finally {
    notificationSaving.value = false;
  }
}
</script>

<template>
  <div class="settings-tab-content security-settings load-error-layout" :aria-busy="loading">
    <AppLoadError :show="Boolean(error)" :retry="load">{{ error }}</AppLoadError>
    <NCard tag="section">
      <div class="settings-section-heading">
        <h3>管理员密码</h3>
        <p>密码用于 Web 管理面的身份认证</p>
      </div>
      <div class="security-password-form">
        <NFormItem
          label="当前密码"
          :label-props="{ for: `${formId}-field-0` }"
          :show-feedback="Boolean(passwordFieldError('currentPassword'))"
          :validation-status="passwordFieldError('currentPassword') ? 'error' : undefined"
          :feedback="passwordFieldError('currentPassword')"
          v-if="configured"
          class="form-field"
        >
          <AppPasswordInput
            v-model:value="currentPassword"
            :disabled="!settingsLoaded || saving"
            :input-props="{ id: `${formId}-field-0`, autocomplete: 'current-password' }"
          />
        </NFormItem>
        <NFormItem
          :label-props="{ for: `${formId}-field-1` }"
          :show-feedback="Boolean(passwordFieldError('newPassword'))"
          :validation-status="passwordFieldError('newPassword') ? 'error' : undefined"
          :feedback="passwordFieldError('newPassword')"
          class="form-field"
        >
          <template #label>{{ configured ? '新密码' : '管理员密码' }}</template>
          <AppPasswordInput
            v-model:value="newPassword"
            :disabled="!settingsLoaded || saving"
            :input-props="{ id: `${formId}-field-1`, autocomplete: 'new-password' }"
          />
        </NFormItem>
        <NFormItem
          :label-props="{ for: `${formId}-field-2` }"
          :show-feedback="Boolean(passwordFieldError('confirmation'))"
          :validation-status="passwordFieldError('confirmation') ? 'error' : undefined"
          :feedback="passwordFieldError('confirmation')"
          class="form-field"
        >
          <template #label>{{ configured ? '确认新密码' : '确认密码' }}</template>
          <AppPasswordInput
            v-model:value="confirmation"
            :disabled="!settingsLoaded || saving"
            :input-props="{ id: `${formId}-field-2`, autocomplete: 'new-password' }"
          />
        </NFormItem>
        <NButton
          :loading="saving"
          attr-type="button"
          :disabled="!settingsLoaded || saving"
          @click="savePassword"
          type="primary"
        >
          {{ saving ? '正在保存' : configured ? '修改密码' : '设置密码' }}
        </NButton>
      </div>
    </NCard>

    <NCard tag="section" class="security-notification-settings">
      <div class="settings-section-heading">
        <h3>安全事件通知</h3>
        <p>发生安全事件发送通知提醒</p>
      </div>
      <div class="security-notification-form">
        <NFormItem
          label="通知渠道"
          :label-props="{ id: (`${formId}-field-3`) + '-label' }"
          :show-feedback="false"
          class="form-field"
        >
          <NSkeleton v-if="initialLoading" size="medium" :sharp="false" />
          <AppSelect
            v-else
            :labelled-by="(`${formId}-field-3`) + '-label'"
            :id="`${formId}-field-3`"
            v-model="notifications.channel"
            :options="channelOptions"
            :disabled="loading || !settingsLoaded || notificationSaving"
            aria-label="通知渠道"
          />
        </NFormItem>
        <NFormItem
          label="通知事件"
          :label-props="{ id: (`${formId}-field-4`) + '-label' }"
          :show-feedback="false"
          class="form-field security-notification-events"
        >
          <NSkeleton v-if="initialLoading" size="medium" :sharp="false" />
          <AppMultiSelect
            v-else
            :labelled-by="(`${formId}-field-4`) + '-label'"
            :id="`${formId}-field-4`"
            :model-value="selectedEvents"
            :options="eventSelectOptions"
            :label="eventsLabel"
            :disabled="loading || !settingsLoaded || notificationSaving"
            :max-height="320"
            aria-label="通知事件"
            @toggle="toggleEvent"
          />
        </NFormItem>
        <div class="settings-actions security-notification-actions">
          <NButton
            :loading="notificationSaving"
            attr-type="button"
            :disabled="loading || !settingsLoaded || notificationSaving"
            @click="saveNotifications"
            type="primary"
          >
            {{ notificationSaving ? '正在保存' : '保存设置' }}
          </NButton>
        </div>
      </div>
    </NCard>
  </div>
</template>

<style>
.security-password-form { display: grid; gap: 16px; }
@media (min-width: 641px) {
  .security-password-form > .n-button { justify-self: end; }
}
.security-notification-form { display: grid; grid-template-columns: minmax(0, 1fr); gap: 16px; }
.security-notification-events { padding-top: 2px; }
.security-notification-actions { margin-top: 2px; }
.settings-actions { display: flex; justify-content: flex-end; padding-top: 4px; }
@media (max-width: 767px) {
  .settings-actions { justify-content: stretch; }
}
</style>
