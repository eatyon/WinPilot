<script setup lang="ts">
import AppLoadError from '../../../components/AppLoadError.vue';
import { useModalState } from '../../../ui/useModalState';
import { useDelayedLoading } from '../../../ui/useDelayedLoading';
import AppTooltip from '../../../components/AppTooltip.vue';
import AppPasswordInput from '../../../components/AppPasswordInput.vue';
import { NFormItem, NButton, NCheckbox, NInput, NCard, NCollapseTransition, NSpin } from 'naive-ui';
import { useId, computed, onBeforeUnmount, reactive, ref } from 'vue';
import { createSettingsRequests } from '../settingsRequests';
import { readApiError, apiFetch, isAbortError, userErrorMessage } from '../../../api/client';
import { showToast } from '../../../ui/toast';
import AppIcon from '../../../components/AppIcon.vue';
import AppSelect from '../../../components/AppSelect.vue';
import CommandBotSettingsModal from '../CommandBotSettingsModal.vue';
import type { CommandBotChannel as BotChannel, CommandBotConfiguration } from '../commandBotModels';
import { safeModeEnabled } from '../../security/safeMode';

const formId = useId();

type QQBotOpenIdTarget = 'user' | 'group';
type QQBotOpenIdStatus = { text: string; tone: 'info' | 'success' | 'error' } | null;
const requests = createSettingsRequests();
const formDefaults = {
  webhookUrl: '',
  webhookSendTo: '',
  telegramBotToken: '',
  telegramChatId: '',
  telegramApiBaseUrl: '',
  telegramProxyUrl: 'http://127.0.0.1:7890',
  telegramProxyEnabled: false,
  workWeixinWebhookUrl: '',
  feishuWebhookUrl: '',
  feishuAppId: '',
  feishuAppSecret: '',
  oneBotEndpoint: '',
  oneBotUserId: '',
  oneBotGroupId: '',
  oneBotToken: '',
  oneBotSendTarget: 'auto',
  qqBotAppId: '',
  qqBotClientSecret: '',
  qqBotOpenId: '',
  qqBotGroupOpenId: '',
  qqBotSendTarget: 'auto',
  qqBotMessageFormat: 'text',
};
type NotificationSettings = typeof formDefaults & Record<string, string | boolean>;
const form = reactive<NotificationSettings>({ ...formDefaults });
const expanded = reactive<Record<string, boolean>>({
  windows: false,
  webhook: false,
  telegram: false,
  workweixin: false,
  feishu: false,
  onebot: false,
  qqbot: false,
});
const loading = ref(false),
  saving = ref(false),
  testing = ref(false),
  error = ref(''),
  binding = ref('');
const qqbotOpenIdStatus = reactive<Record<QQBotOpenIdTarget, QQBotOpenIdStatus>>({
  user: null,
  group: null,
});
const savingChannel = ref('');
const loadingChannel = ref('');
const testingChannel = ref('');
let qqbotOpenIdController: AbortController | undefined;
const botEnabled = reactive<Record<BotChannel, boolean>>({
  telegram: false,
  feishu: false,
  qqbot: false,
  onebot: false,
});
const botsLoaded = ref(false);
const botConfiguration = ref<CommandBotConfiguration>();
const botDialogModal = useModalState<BotChannel | undefined>(() => undefined, Boolean, () => {
  botConfiguration.value = undefined;
});
const botDialog = botDialogModal.data;
const loadingBotChannel = ref('');
const showBotLoading = useDelayedLoading(() => Boolean(loadingBotChannel.value));
async function openBotSettings(channel: string) {
  if (
    !requests.active || safeModeEnabled.value || loading.value || !settingsLoaded.value ||
    !botsLoaded.value || saving.value || testing.value || loadingBotChannel.value || botDialogModal.show
  ) return;
  if (!['telegram', 'feishu', 'qqbot', 'onebot'].includes(channel)) return;
  loadingBotChannel.value = channel;
  try {
    const response = await requests.fetch(`/api/settings/notifications/command-bots/${channel}`);
    if (!response.ok) throw new Error(await readApiError(response, '无法读取命令机器人设置', requests.json));
    const configuration = await requests.json<CommandBotConfiguration>(response);
    if (safeModeEnabled.value) return;
    botConfiguration.value = configuration;
    botDialogModal.open(channel as BotChannel);
  } catch (cause) {
    if (requests.active && !isAbortError(cause)) showToast(userErrorMessage(cause, '无法读取命令机器人设置'), 'error');
  } finally {
    loadingBotChannel.value = '';
  }
}
const botFields: Record<BotChannel, string[]> = {
  telegram: [
    'telegramBotToken',
    'telegramChatId',
    'telegramApiBaseUrl',
    'telegramProxyUrl',
    'telegramProxyEnabled',
  ],
  feishu: ['feishuAppId', 'feishuAppSecret'],
  qqbot: [
    'qqBotAppId',
    'qqBotClientSecret',
    'qqBotOpenId',
    'qqBotGroupOpenId',
    'qqBotMessageFormat',
  ],
  onebot: ['oneBotUserId', 'oneBotGroupId'],
};
const savedSettings = reactive<Partial<NotificationSettings>>({});
const sendTargetOptions = [
  { value: 'auto', label: '全部' },
  { value: 'private', label: '私聊' },
  { value: 'group', label: '群聊' },
];
const messageFormatOptions = [
  { value: 'text', label: '普通消息' },
  { value: 'markdown', label: 'Markdown' },
];
const connectionDirty = computed(() =>
  botDialog.value
    ? botFields[botDialog.value].some((key) => form[key] !== savedSettings[key])
    : false,
);
const settingsLoaded = ref(false);
const channels = [
  { key: 'windows', title: 'Windows 通知', desc: '使用系统通知中心接收消息', prefix: 'windows' },
  { key: 'webhook', title: 'Webhook', desc: 'Webhook 相关设置', prefix: 'webhook' },
  { key: 'telegram', title: 'Telegram', desc: 'Telegram 机器人相关设置', prefix: 'telegram' },
  { key: 'workweixin', title: '企业微信', desc: '企业微信通知相关设置', prefix: 'workWeixin' },
  { key: 'feishu', title: '飞书', desc: '飞书通知相关设置', prefix: 'feishu' },
  { key: 'onebot', title: 'OneBot', desc: 'OneBot 通知相关设置', prefix: 'oneBot' },
  { key: 'qqbot', title: 'QQBot', desc: 'QQ 官方 Bot 通知设置', prefix: 'qqBot' },
] as const;
async function loadSettings() {
  if (!requests.active || safeModeEnabled.value || loading.value) return;
  loading.value = true;
  try {
    const [response, botsResponse] = await Promise.all([
      requests.fetch('/api/settings/notifications'),
      requests.fetch('/api/settings/notifications/command-bots'),
    ]);
    if (!response.ok)
      throw new Error(await readApiError(response, '无法读取通知设置', requests.json));
    Object.assign(form, await requests.json(response));
    Object.assign(savedSettings, form);
    settingsLoaded.value = true;
    if (!botsResponse.ok)
      throw new Error(await readApiError(botsResponse, '无法读取命令机器人开关', requests.json));
    Object.assign(botEnabled, await requests.json(botsResponse));
    botsLoaded.value = true;
    error.value = '';
  } catch (cause) {
    if (!requests.active || isAbortError(cause)) return;
    error.value = userErrorMessage(cause, '无法读取通知设置');
  } finally {
    loading.value = false;
  }
}
async function toggleChannel(channel: string) {
  if (!requests.active || safeModeEnabled.value || loading.value || loadingBotChannel.value === channel) return;
  if (expanded[channel]) {
    expanded[channel] = false;
    return;
  }
  loadingChannel.value = channel;
  try {
    if (!settingsLoaded.value || !botsLoaded.value) await loadSettings();
    if (
      requests.active && !safeModeEnabled.value &&
      settingsLoaded.value && botsLoaded.value && !error.value
    )
      expanded[channel] = true;
  } finally {
    loadingChannel.value = '';
  }
}
async function save(channel: string) {
  if (!settingsLoaded.value || saving.value || loadingBotChannel.value === channel || safeModeEnabled.value || !requests.active) return;
  saving.value = true;
  savingChannel.value = channel;
  const submitted = { ...form };
  try {
    const response = await requests.fetch(`/api/settings/notifications/${channel}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(submitted),
    });
    if (!response.ok)
      throw new Error(await readApiError(response, '无法保存通知设置', requests.json));
    const result = await requests.json<NotificationSettings>(response);
    const prefix = channels.find((item) => item.key === channel)?.prefix;
    if (!prefix) throw new Error('通知渠道无效');
    for (const key of Object.keys(form)) {
      if (!key.startsWith(prefix)) continue;
      if (form[key] === submitted[key]) form[key] = result[key];
      savedSettings[key] = result[key];
    }
    error.value = '';
    showToast('通知设置已保存', 'success');
  } catch (cause) {
    if (!requests.active || isAbortError(cause)) return;
    showToast(userErrorMessage(cause, '无法保存通知设置'));
  } finally {
    saving.value = false;
    savingChannel.value = '';
  }
}
async function test(channel: string) {
  if (
    !settingsLoaded.value ||
    testing.value ||
    saving.value ||
    loadingBotChannel.value === channel ||
    safeModeEnabled.value ||
    !requests.active
  )
    return;
  testing.value = true;
  testingChannel.value = channel;
  try {
    const response = await requests.fetch('/api/notifications/test', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ content: '这是一条测试通知信息', channel }),
    });
    if (!response.ok) throw new Error(await readApiError(response, '通知测试失败', requests.json));
    const results = await requests.json<{ succeeded?: boolean; channel?: string }[]>(response);
    if (!Array.isArray(results)) throw new Error('通知测试返回的数据无效');
    const failed = results.filter(item => item.succeeded === false);
    if (failed.length > 0)
      throw Error(`通知渠道发送失败：${failed.map(item => item.channel).join('、')}`);
    showToast('测试通知已发送', 'success');
  } catch (cause) {
    if (!requests.active || isAbortError(cause)) return;
    showToast(userErrorMessage(cause, '通知测试失败'));
  } finally {
    testing.value = false;
    testingChannel.value = '';
  }
}
async function bindQQBot(target: QQBotOpenIdTarget) {
  if (!settingsLoaded.value || safeModeEnabled.value || !requests.active) return;
  if (binding.value === target) {
    qqbotOpenIdStatus[target] = null;
    qqbotOpenIdController?.abort();
    binding.value = '';
    return;
  }
  if (binding.value) return;

  const controller = new AbortController();
  qqbotOpenIdController = controller;
  binding.value = target;
  qqbotOpenIdStatus[target] = { text: '正在连接 QQBot…', tone: 'info' };
  try {
    const response = await apiFetch(`/api/notifications/qqbot/bind-${target}`, {
      method: 'POST',
      signal: controller.signal,
    });
    if (!response.ok || !response.body) throw Error('OpenID 获取失败');
    const reader = response.body.getReader();
    try {
      const decoder = new TextDecoder();
      let buffer = '';
      while (true) {
        const chunk = await reader.read();
        requests.ensureActive();
        if (controller.signal.aborted) throw new DOMException('OpenID 获取已取消', 'AbortError');
        if (chunk.done) break;
        buffer += decoder.decode(chunk.value, { stream: true });
        const lines = buffer.split('\n');
        buffer = lines.pop() ?? '';
        for (const line of lines) {
          if (!line.startsWith('data: ')) continue;
          const data = JSON.parse(line.slice(6));
          if (data.type === 'code')
            qqbotOpenIdStatus[target] = {
              text: `请发送验证码 ${data.code} 获取 OpenID`,
              tone: 'info',
            };
          if (data.type === 'success') {
            if (target === 'user') form.qqBotOpenId = data.openId;
            else form.qqBotGroupOpenId = data.openId;
            qqbotOpenIdStatus[target] = { text: 'OpenID 获取成功，请点击“保存”', tone: 'success' };
          }
          if (data.type === 'error') throw Error(data.message);
        }
      }
    } finally {
      await reader.cancel().catch(() => {});
      reader.releaseLock();
    }
  } catch (cause) {
    if (!requests.active || controller.signal.aborted || isAbortError(cause)) return;
    const message = userErrorMessage(cause, 'OpenID 获取失败');
    qqbotOpenIdStatus[target] = message ? { text: message, tone: 'error' } : null;
  } finally {
    if (qqbotOpenIdController === controller) {
      qqbotOpenIdController = undefined;
      binding.value = '';
    }
  }
}
onBeforeUnmount(() => {
  requests.dispose();
  qqbotOpenIdController?.abort();
});
</script>
<template>
  <div class="settings-tab-content notification-settings-content load-error-layout">
    <AppLoadError :show="Boolean(error)" :retry="!settingsLoaded || !botsLoaded ? loadSettings : undefined">{{ error }}</AppLoadError>
    <NCard v-for="channel in channels" :key="channel.key" :header-style="{ padding: 0 }" :content-style="{ padding: 0 }">
      <template #header>
        <NButton
          class="notification-channel-trigger"
          attr-type="button"
          text
          block
          :aria-expanded="Boolean(expanded[channel.key])"
          :aria-busy="loading && loadingChannel === channel.key"
          :disabled="loadingBotChannel === channel.key || savingChannel === channel.key"
          @click="toggleChannel(channel.key)"
        >
          <span class="notification-channel-heading">
            <span class="notification-channel-copy">
              <strong>{{ channel.title }}</strong>
              <small>{{ channel.desc }}</small>
            </span>
            <NSpin v-if="loading && loadingChannel === channel.key" :size="18" aria-label="正在读取通知设置" />
            <AppIcon v-else :name="expanded[channel.key] ? 'chevron-down' : 'chevron-right'" />
          </span>
        </NButton>
      </template>
      <NCollapseTransition :show="Boolean(expanded[channel.key])" :inert="!expanded[channel.key] || undefined">
        <div class="notification-channel-content">
          <div
            class="notification-channel-body"
            :inert="savingChannel === channel.key"
            v-show="settingsLoaded && botsLoaded"
          >
            <template v-if="channel.key === 'webhook'">
              <NFormItem
                label="Webhook 端点"
                :label-props="{ for: `${formId}-field-0-${channel.key}` }"
                :show-feedback="false"
                class="form-field"
              >
                <NInput :input-props="{ id: `${formId}-field-0-${channel.key}` }" v-model:value="form.webhookUrl" placeholder="填写 Webhook 端点" />
              </NFormItem>
              <NFormItem
                label="发送对象"
                :label-props="{ for: `${formId}-field-1-${channel.key}` }"
                :show-feedback="false"
                class="form-field"
              >
                <NInput :input-props="{ id: `${formId}-field-1-${channel.key}` }" v-model:value="form.webhookSendTo" placeholder="填写发送对象" />
              </NFormItem>
            </template>
            <template v-else-if="channel.key === 'telegram'">
              <NFormItem
                label="机器人 Token"
                :label-props="{ for: `${formId}-field-2-${channel.key}` }"
                :show-feedback="false"
                class="form-field"
              >
                <AppPasswordInput
                  :input-props="{ id: `${formId}-field-2-${channel.key}` }"
                  v-model:value="form.telegramBotToken"
                  placeholder="填写 Telegram 机器人的 Token"
                />
              </NFormItem>
              <NFormItem
                label="聊天 ID"
                :label-props="{ for: `${formId}-field-3-${channel.key}` }"
                :show-feedback="false"
                class="form-field"
              >
                <NInput :input-props="{ id: `${formId}-field-3-${channel.key}` }" v-model:value="form.telegramChatId" placeholder="填写接收消息的聊天 ID" />
              </NFormItem>
              <NFormItem
                label="API 基础 URL（可选）"
                :label-props="{ for: `${formId}-field-4-${channel.key}` }"
                :show-feedback="false"
                class="form-field"
              >
                <NInput
                  :input-props="{ id: `${formId}-field-4-${channel.key}` }"
                  v-model:value="form.telegramApiBaseUrl"
                  placeholder="留空使用官方 API，或填写第三方 API 地址"
                />
              </NFormItem>
              <NFormItem
                label="代理地址"
                :label-props="{ for: `${formId}-field-5-${channel.key}` }"
                :show-feedback="false"
                class="form-field"
              >
                <NInput
                  :input-props="{ id: `${formId}-field-5-${channel.key}` }"
                  v-model:value="form.telegramProxyUrl"
                  placeholder="格式：http://127.0.0.1:7890"
                />
              </NFormItem>
              <NCheckbox
                v-model:checked="form.telegramProxyEnabled"
              ><span>启用代理</span></NCheckbox
              >
            </template>
            <template v-else-if="channel.key === 'workweixin'">
              <NFormItem
                label="企业微信通知地址"
                :label-props="{ for: `${formId}-field-6-${channel.key}` }"
                :show-feedback="false"
                class="form-field"
              >
                <NInput
                  :input-props="{ id: `${formId}-field-6-${channel.key}` }"
                  v-model:value="form.workWeixinWebhookUrl"
                  placeholder="填写企业微信通知地址"
                />
              </NFormItem>
            </template>
            <template v-else-if="channel.key === 'feishu'">
              <NFormItem
                label="飞书通知地址"
                :label-props="{ for: `${formId}-field-7-${channel.key}` }"
                :show-feedback="false"
                class="form-field"
              >
                <NInput :input-props="{ id: `${formId}-field-7-${channel.key}` }" v-model:value="form.feishuWebhookUrl" placeholder="填写飞书通知地址" />
              </NFormItem>
              <NFormItem
                label="飞书 AppId"
                :label-props="{ for: `${formId}-field-8-${channel.key}` }"
                :show-feedback="false"
                class="form-field"
              >
                <NInput
                  :input-props="{ id: `${formId}-field-8-${channel.key}` }"
                  v-model:value="form.feishuAppId"
                  placeholder="发送图片需填写 AppId 和 AppSecret"
                />
              </NFormItem>
              <NFormItem
                label="飞书 AppSecret"
                :label-props="{ for: `${formId}-field-9-${channel.key}` }"
                :show-feedback="false"
                class="form-field"
              >
                <AppPasswordInput
                  :input-props="{ id: `${formId}-field-9-${channel.key}` }"
                  v-model:value="form.feishuAppSecret"
                  placeholder="发送图片需填写 AppId 和 AppSecret"
                />
              </NFormItem>
            </template>
            <template v-else-if="channel.key === 'onebot'">
              <NFormItem
                label="OneBot 请求地址"
                :label-props="{ for: `${formId}-field-10-${channel.key}` }"
                :show-feedback="false"
                class="form-field"
              >
                <NInput :input-props="{ id: `${formId}-field-10-${channel.key}` }" v-model:value="form.oneBotEndpoint" placeholder="填写 OneBot 请求地址" />
              </NFormItem>
              <NFormItem
                label="QQ 号"
                :label-props="{ for: `${formId}-field-11-${channel.key}` }"
                :show-feedback="false"
                class="form-field"
              >
                <NInput :input-props="{ id: `${formId}-field-11-${channel.key}` }" v-model:value="form.oneBotUserId" placeholder="填写接收消息的 QQ 号" />
              </NFormItem>
              <NFormItem
                label="群号"
                :label-props="{ for: `${formId}-field-12-${channel.key}` }"
                :show-feedback="false"
                class="form-field"
              >
                <NInput :input-props="{ id: `${formId}-field-12-${channel.key}` }" v-model:value="form.oneBotGroupId" placeholder="填写接收消息的群号" />
              </NFormItem>
              <NFormItem
                label="OneBot Token"
                :label-props="{ for: `${formId}-field-13-${channel.key}` }"
                :show-feedback="false"
                class="form-field"
              >
                <AppPasswordInput
                  :input-props="{ id: `${formId}-field-13-${channel.key}` }"
                  v-model:value="form.oneBotToken"
                  placeholder="填写 OneBot Token（可选）"
                />
              </NFormItem>
            </template>
            <template v-else-if="channel.key === 'qqbot'">
              <NFormItem
                label="AppID"
                :label-props="{ for: `${formId}-field-14-${channel.key}` }"
                :show-feedback="false"
                class="form-field"
              >
                <NInput :input-props="{ id: `${formId}-field-14-${channel.key}` }" v-model:value="form.qqBotAppId" placeholder="QQ 开放平台的 AppID" />
              </NFormItem>
              <NFormItem
                label="AppSecret"
                :label-props="{ for: `${formId}-field-15-${channel.key}` }"
                :show-feedback="false"
                class="form-field"
              >
                <AppPasswordInput
                  :input-props="{ id: `${formId}-field-15-${channel.key}` }"
                  v-model:value="form.qqBotClientSecret"
                  placeholder="QQ 开放平台的 AppSecret"
                />
              </NFormItem>
              <NFormItem
                label="用户 OpenID"
                :label-props="{ for: `${formId}-field-16-${channel.key}` }"
                :show-feedback="Boolean(qqbotOpenIdStatus.user)"
                :validation-status="qqbotOpenIdStatus.user?.tone === 'info' ? undefined : qqbotOpenIdStatus.user?.tone"
                class="form-field"
              >
                <div class="notification-inline-field">
                  <NInput
                    :input-props="{ id: `${formId}-field-16-${channel.key}` }"
                    v-model:value="form.qqBotOpenId"
                    placeholder="可点击「获取」私聊机器人发送验证码自动填充"
                  />
                  <NButton
                    attr-type="button"
                    :disabled="binding !== '' && binding !== 'user'"
                    @click="bindQQBot('user')"
                  >
                    {{ binding === 'user' ? '取消' : '获取' }}
                  </NButton>
                </div>
                <template #feedback><span :role="qqbotOpenIdStatus.user?.tone === 'error' ? 'alert' : 'status'">{{ qqbotOpenIdStatus.user?.text }}</span></template>
              </NFormItem>
              <NFormItem
                label="群 OpenID"
                :label-props="{ for: `${formId}-field-17-${channel.key}` }"
                :show-feedback="Boolean(qqbotOpenIdStatus.group)"
                :validation-status="qqbotOpenIdStatus.group?.tone === 'info' ? undefined : qqbotOpenIdStatus.group?.tone"
                class="form-field"
              >
                <div class="notification-inline-field">
                  <NInput
                    :input-props="{ id: `${formId}-field-17-${channel.key}` }"
                    v-model:value="form.qqBotGroupOpenId"
                    placeholder="可点击「获取」群聊@机器人发送验证码自动填充"
                  />
                  <NButton
                    attr-type="button"
                    :disabled="binding !== '' && binding !== 'group'"
                    @click="bindQQBot('group')"
                  >
                    {{ binding === 'group' ? '取消' : '获取' }}
                  </NButton>
                </div>
                <template #feedback><span :role="qqbotOpenIdStatus.group?.tone === 'error' ? 'alert' : 'status'">{{ qqbotOpenIdStatus.group?.text }}</span></template>
              </NFormItem>
            </template>
            <NFormItem
              label="发送目标"
              :label-props="{ id: (`${formId}-field-18-${channel.key}`) + '-label' }"
              :show-feedback="false"
              v-if="channel.key === 'onebot' || channel.key === 'qqbot'"
              class="form-field"
            >
              <AppSelect
                :labelled-by="(`${formId}-field-18-${channel.key}`) + '-label'"
                :id="`${formId}-field-18-${channel.key}`"
                v-model="form[channel.key === 'onebot' ? 'oneBotSendTarget' : 'qqBotSendTarget']"
                :options="sendTargetOptions"
                :disabled="loading || !settingsLoaded || saving || safeModeEnabled"
                aria-label="发送目标"
              />
            </NFormItem>
            <NFormItem
              label="消息格式"
              :label-props="{ id: (`${formId}-field-19-${channel.key}`) + '-label' }"
              :show-feedback="false"
              v-if="channel.key === 'qqbot'"
              class="form-field"
            >
              <AppSelect
                :labelled-by="(`${formId}-field-19-${channel.key}`) + '-label'"
                :id="`${formId}-field-19-${channel.key}`"
                v-model="form.qqBotMessageFormat"
                :options="messageFormatOptions"
                :disabled="loading || !settingsLoaded || saving || safeModeEnabled"
                aria-label="消息格式"
              />
            </NFormItem>
            <div class="notification-channel-actions">
              <span
                v-if="
                  channel.key === 'telegram' ||
                  channel.key === 'feishu' ||
                  channel.key === 'qqbot' ||
                  channel.key === 'onebot'
                "
                class="command-bot-entry"
              >
                <AppTooltip
                  text="命令机器人设置"
                  :disabled-trigger="loading || !settingsLoaded || saving || testing || !botsLoaded || safeModeEnabled || Boolean(loadingBotChannel)"
                >
                  <NButton
                    attr-type="button"
                    class="app-icon-button command-bot-button"
                    :loading="loadingBotChannel === channel.key && showBotLoading"
                    :aria-busy="loadingBotChannel === channel.key"
                    :disabled="loading || !settingsLoaded || saving || testing || !botsLoaded || safeModeEnabled || Boolean(loadingBotChannel && (loadingBotChannel !== channel.key || !showBotLoading))"
                    aria-label="命令机器人设置"
                    @click="openBotSettings(channel.key)"
                  >
                    <template #icon><AppIcon name="bot" :color="botEnabled[channel.key] ? 'var(--accent-text)' : undefined" /></template>
                  </NButton>
                </AppTooltip>
              </span>
              <NButton
                :loading="testingChannel === channel.key"
                :disabled="loading || !settingsLoaded || saving || testing || loadingBotChannel === channel.key"
                @click="test(channel.key)"
              >
                {{ testingChannel === channel.key ? '正在发送' : '测试发送' }}
              </NButton>
              <NButton
                v-if="channel.key !== 'windows'"
                :loading="savingChannel === channel.key"
                :disabled="loading || !settingsLoaded || saving || loadingBotChannel === channel.key"
                @click="save(channel.key)"
                type="primary"
              >
                {{ savingChannel === channel.key ? '正在保存' : '保存' }}
              </NButton>
            </div>
          </div>
        </div>
      </NCollapseTransition>
    </NCard>
    <CommandBotSettingsModal
      :show="botDialogModal.show"
      @after-leave="botDialogModal.afterLeave"
      v-if="botDialog && botConfiguration"
      :key="botDialog"
      :channel="botDialog"
      :initial-configuration="botConfiguration"
      @enabled-change="botEnabled[botDialog] = $event"
      :connection-dirty="connectionDirty"
      @close="botDialogModal.close()"
    />
  </div>
</template>

<style>
.notification-settings-content { grid-template-columns: minmax(0, 1fr); }
.notification-channel-copy { display: grid; min-width: 0; flex: 1; gap: 3px; }
.notification-channel-copy strong { font-size: 16px; font-weight: 500; }
.notification-channel-copy small { overflow: hidden; color: var(--text-muted); font-size: 14px; text-overflow: ellipsis; white-space: nowrap; transition: color .3s cubic-bezier(.4, 0, .2, 1); }
.notification-channel-body { display: grid; gap: 12px; }
.notification-channel-actions { display: flex; justify-content: flex-end; gap: 8px; }
.notification-inline-field { display: flex; width: 100%; min-width: 0; gap: 8px; }
.notification-inline-field > .n-input { min-width: 0; flex: 1; }
.notification-inline-field > .n-button { flex: 0 0 auto; }
</style>

<style scoped>
.notification-channel-trigger {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  min-width: 0;
  height: auto;
  padding: var(--n-padding-top) var(--n-padding-left) var(--n-padding-bottom);
  text-align: left;
  line-height: 1.5;
}
.notification-channel-heading {
  display: flex;
  flex: 1;
  align-items: center;
  min-width: 0;
  gap: 12px;
}
.notification-channel-heading > .app-icon {
  flex-shrink: 0;
}
.notification-channel-content {
  padding: 0 var(--n-padding-left) var(--n-padding-bottom);
}
.command-bot-entry {
  display: inline-flex;
  margin-right: auto;
  flex: 0 0 auto;
}
</style>
