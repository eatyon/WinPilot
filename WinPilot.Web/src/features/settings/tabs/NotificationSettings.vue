<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref } from 'vue';
import { createSettingsRequests } from '../SettingsRequests';
import { readApiError, apiFetch, isAbortError, userErrorMessage } from '../../../api/client';
import { showToast } from '../../../ui/toast';
import AppIcon from '../../../components/AppIcon.vue';
import AppSelect from '../../../components/AppSelect.vue';
import CommandBotSettingsModal from '../CommandBotSettingsModal.vue';
import { safeModeEnabled } from '../../security/safeMode';

type NotificationSettings = Record<string, any>;
type QQBotOpenIdTarget = 'user' | 'group';
type QQBotOpenIdStatus = { text: string; tone: 'info' | 'success' | 'error' } | null;
const requests = createSettingsRequests();
const form = reactive<NotificationSettings>({
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
});
const expanded = reactive<Record<string, boolean>>({
  windows: false,
  webhook: false,
  telegram: false,
  workweixin: false,
  feishu: false,
  onebot: false,
  qqbot: false,
});
const passwordVisible = reactive<Record<string, boolean>>({
  telegramBotToken: false,
  feishuAppSecret: false,
  oneBotToken: false,
  qqBotClientSecret: false,
});
const loading = ref(false),
  saving = ref(false),
  testing = ref(false),
  error = ref(''),
  binding = ref('');
const qqbotOpenIdStatus = reactive<Record<QQBotOpenIdTarget, QQBotOpenIdStatus>>({ user: null, group: null });
const savingChannel = ref('');
const testingChannel = ref('');
let qqbotOpenIdController: AbortController | undefined;
type BotChannel = 'telegram' | 'feishu' | 'qqbot' | 'onebot';
const botEnabled = reactive<Record<BotChannel, boolean>>({ telegram: false, feishu: false, qqbot: false, onebot: false });
const botsLoaded = ref(false);
const botDialog = ref<BotChannel>();
const botFields: Record<BotChannel, string[]> = {
  telegram: ['telegramBotToken', 'telegramChatId', 'telegramApiBaseUrl', 'telegramProxyUrl', 'telegramProxyEnabled'],
  feishu: ['feishuAppId', 'feishuAppSecret'],
  qqbot: ['qqBotAppId', 'qqBotClientSecret', 'qqBotOpenId', 'qqBotGroupOpenId', 'qqBotMessageFormat'],
  onebot: ['oneBotUserId', 'oneBotGroupId'],
};
const savedSettings = reactive<NotificationSettings>({});
const sendTargetOptions = [
  { value: 'auto', label: '全部' },
  { value: 'private', label: '私聊' },
  { value: 'group', label: '群聊' },
];
const messageFormatOptions = [
  { value: 'text', label: '普通消息' },
  { value: 'markdown', label: 'Markdown' },
];
const connectionDirty = computed(() => botDialog.value
  ? botFields[botDialog.value].some((key) => form[key] !== savedSettings[key]) : false);
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
  if (safeModeEnabled.value) {
    loading.value = false;
    return;
  }
  loading.value = true;
  settingsLoaded.value = botsLoaded.value = false;
  error.value = '';
  try {
    const response = await requests.fetch('/api/settings/notifications');
    if (!response.ok) throw new Error(await readApiError(response, '无法读取通知设置', requests.json));
    Object.assign(form, await requests.json(response));
    Object.assign(savedSettings, form);
    settingsLoaded.value = true;
    const botsResponse = await requests.fetch('/api/settings/notifications/command-bots');
    if (!botsResponse.ok) throw new Error(await readApiError(botsResponse, '无法读取命令机器人开关', requests.json));
    Object.assign(botEnabled, await requests.json(botsResponse));
    botsLoaded.value = true;
  } catch (cause) {
    if (!requests.active || isAbortError(cause)) return;
    error.value = userErrorMessage(cause, '无法读取通知设置');
  } finally {
    loading.value = false;
  }
}
async function toggleChannel(channel: string) {
  if (!requests.active || safeModeEnabled.value || loading.value) return;
  if (expanded[channel]) {
    expanded[channel] = false;
    return;
  }
  if (!settingsLoaded.value || !botsLoaded.value) await loadSettings();
  if (requests.active && !safeModeEnabled.value && settingsLoaded.value && botsLoaded.value)
    expanded[channel] = true;
}
async function save(channel: string) {
  if (!settingsLoaded.value || saving.value || safeModeEnabled.value || !requests.active) return;
  saving.value = true;
  savingChannel.value = channel;
  const submitted = { ...form };
  try {
    const response = await requests.fetch(`/api/settings/notifications/${channel}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(submitted),
    });
    if (!response.ok) throw new Error(await readApiError(response, '无法保存通知设置', requests.json));
    const result = await requests.json<NotificationSettings>(response);
    const prefix = channels.find(item => item.key === channel)?.prefix;
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
    error.value = userErrorMessage(cause, '无法保存通知设置');
    showToast(error.value);
  } finally {
    saving.value = false;
    savingChannel.value = '';
  }
}
function togglePassword(field: string) {
  passwordVisible[field] = !passwordVisible[field];
}
async function test(channel: string) {
  if (!settingsLoaded.value || testing.value || saving.value || safeModeEnabled.value || !requests.active) return;
  testing.value = true;
  testingChannel.value = channel;
  try {
    const response = await requests.fetch('/api/notifications/test', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ content: '这是一条测试通知信息', channel }),
    });
    if (!response.ok) throw new Error(await readApiError(response, '通知测试失败', requests.json));
    const results = await requests.json(response);
    if (!Array.isArray(results)) throw new Error('通知测试返回的数据无效');
    const failed = results.filter((item: { succeeded?: boolean }) => item.succeeded === false);
    if (failed.length > 0)
      throw Error(`通知渠道发送失败：${failed.map((item: any) => item.channel).join('、')}`);
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
            qqbotOpenIdStatus[target] = { text: `请发送验证码 ${data.code} 获取 OpenID`, tone: 'info' };
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
onBeforeUnmount(() => { requests.dispose(); qqbotOpenIdController?.abort(); });
</script>
<template>
  <div class="settings-tab-content notification-settings-content">
    <p v-if="error" class="status-message error">{{ error }}</p>
    <section
      v-for="channel in channels"
      :key="channel.key"
      class="settings-section notification-channel-section"
      :class="{ expanded: expanded[channel.key] }"
    >
      <button
        class="notification-channel-header"
        type="button"
        :aria-expanded="expanded[channel.key] === true"
        @click="toggleChannel(channel.key)"
      >
        <span class="notification-channel-copy">
          <strong>{{ channel.title }}</strong>
          <small>{{ channel.desc }}</small>
        </span>
        <span class="notification-chevron" :class="{ open: expanded[channel.key] }" aria-hidden="true" />
      </button>
      <div v-if="expanded[channel.key]" class="notification-channel-body" :class="{ 'settings-data-pending': !settingsLoaded }">
        <template v-if="channel.key === 'webhook'">
          <label class="field-control">
            <span>Webhook 端点</span>
            <input v-model="form.webhookUrl" placeholder="填写 Webhook 端点" />
          </label>
          <label class="field-control">
            <span>发送对象</span>
            <input v-model="form.webhookSendTo" placeholder="填写发送对象" />
          </label>
        </template>
        <template v-else-if="channel.key === 'telegram'">
          <label class="field-control">
            <span>机器人 Token</span>
            <span class="secret-input">
              <input
                v-model="form.telegramBotToken"
                :type="passwordVisible.telegramBotToken ? 'text' : 'password'"
                placeholder="填写 Telegram 机器人的 Token"
              />
              <button
                type="button"
                class="secret-toggle"
                :aria-label="passwordVisible.telegramBotToken ? '隐藏 Token' : '显示 Token'"
                @click="togglePassword('telegramBotToken')"
              >
                <AppIcon :name="passwordVisible.telegramBotToken ? 'eye-off' : 'eye'" />
              </button>
            </span>
          </label>
          <label class="field-control">
            <span>聊天 ID</span>
            <input v-model="form.telegramChatId" placeholder="填写接收消息的聊天 ID" />
          </label>
          <label class="field-control">
            <span>API 基础 URL（可选）</span>
            <input v-model="form.telegramApiBaseUrl" placeholder="留空使用官方 API，或填写第三方 API 地址" />
          </label>
          <label class="field-control">
            <span>代理地址</span>
            <input v-model="form.telegramProxyUrl" placeholder="格式：http://127.0.0.1:7890" />
          </label>
          <label class="toggle-control">
            <input v-model="form.telegramProxyEnabled" type="checkbox" />
            <span>启用代理</span>
          </label>
        </template>
        <template v-else-if="channel.key === 'workweixin'">
          <label class="field-control">
            <span>企业微信通知地址</span>
            <input v-model="form.workWeixinWebhookUrl" placeholder="填写企业微信通知地址" />
          </label>
        </template>
        <template v-else-if="channel.key === 'feishu'">
          <label class="field-control">
            <span>飞书通知地址</span>
            <input v-model="form.feishuWebhookUrl" placeholder="填写飞书通知地址" />
          </label>
          <label class="field-control">
            <span>飞书 AppId</span>
            <input v-model="form.feishuAppId" placeholder="发送图片需填写 AppId 和 AppSecret" />
          </label>
          <label class="field-control">
            <span>飞书 AppSecret</span>
            <span class="secret-input">
              <input
                v-model="form.feishuAppSecret"
                :type="passwordVisible.feishuAppSecret ? 'text' : 'password'"
                placeholder="发送图片需填写 AppId 和 AppSecret"
              />
              <button
                type="button"
                class="secret-toggle"
                :aria-label="passwordVisible.feishuAppSecret ? '隐藏 AppSecret' : '显示 AppSecret'"
                @click="togglePassword('feishuAppSecret')"
              >
                <AppIcon :name="passwordVisible.feishuAppSecret ? 'eye-off' : 'eye'" />
              </button>
            </span>
          </label>
        </template>
        <template v-else-if="channel.key === 'onebot'">
          <label class="field-control">
            <span>OneBot 请求地址</span>
            <input v-model="form.oneBotEndpoint" placeholder="填写 OneBot 请求地址" />
          </label>
          <label class="field-control">
            <span>QQ 号</span>
            <input v-model="form.oneBotUserId" placeholder="填写接收消息的 QQ 号" />
          </label>
          <label class="field-control">
            <span>群号</span>
            <input v-model="form.oneBotGroupId" placeholder="填写接收消息的群号" />
          </label>
          <label class="field-control">
            <span>OneBot Token</span>
            <span class="secret-input">
              <input
                v-model="form.oneBotToken"
                :type="passwordVisible.oneBotToken ? 'text' : 'password'"
                placeholder="填写 OneBot Token（可选）"
              />
              <button
                type="button"
                class="secret-toggle"
                :aria-label="passwordVisible.oneBotToken ? '隐藏 Token' : '显示 Token'"
                @click="togglePassword('oneBotToken')"
              >
                <AppIcon :name="passwordVisible.oneBotToken ? 'eye-off' : 'eye'" />
              </button>
            </span>
          </label>
        </template>
        <template v-else-if="channel.key === 'qqbot'">
          <label class="field-control">
            <span>AppID</span>
            <input v-model="form.qqBotAppId" placeholder="QQ 开放平台的 AppID" />
          </label>
          <label class="field-control">
            <span>AppSecret</span>
            <span class="secret-input">
              <input
                v-model="form.qqBotClientSecret"
                :type="passwordVisible.qqBotClientSecret ? 'text' : 'password'"
                placeholder="QQ 开放平台的 AppSecret"
              />
              <button
                type="button"
                class="secret-toggle"
                :aria-label="passwordVisible.qqBotClientSecret ? '隐藏 AppSecret' : '显示 AppSecret'"
                @click="togglePassword('qqBotClientSecret')"
              >
                <AppIcon :name="passwordVisible.qqBotClientSecret ? 'eye-off' : 'eye'" />
              </button>
            </span>
          </label>
          <label class="field-control">
            <span>用户 OpenID</span>
            <div class="notification-inline-field">
              <input v-model="form.qqBotOpenId" placeholder="可点击「获取」私聊机器人发送验证码自动填充" />
              <button
                class="button button-secondary"
                type="button"
                :disabled="binding !== '' && binding !== 'user'"
                @click="bindQQBot('user')"
              >
                {{ binding === 'user' ? '取消' : '获取' }}
              </button>
            </div>
            <small
              v-if="qqbotOpenIdStatus.user"
              class="notification-field-status"
              :class="qqbotOpenIdStatus.user.tone"
              :role="qqbotOpenIdStatus.user.tone === 'error' ? 'alert' : 'status'"
            >
              {{ qqbotOpenIdStatus.user.text }}
            </small>
          </label>
          <label class="field-control">
            <span>群 OpenID</span>
            <div class="notification-inline-field">
              <input
                v-model="form.qqBotGroupOpenId"
                placeholder="可点击「获取」群聊@机器人发送验证码自动填充"
              />
              <button
                class="button button-secondary"
                type="button"
                :disabled="binding !== '' && binding !== 'group'"
                @click="bindQQBot('group')"
              >
                {{ binding === 'group' ? '取消' : '获取' }}
              </button>
            </div>
            <small
              v-if="qqbotOpenIdStatus.group"
              class="notification-field-status"
              :class="qqbotOpenIdStatus.group.tone"
              :role="qqbotOpenIdStatus.group.tone === 'error' ? 'alert' : 'status'"
            >
              {{ qqbotOpenIdStatus.group.text }}
            </small>
          </label>
        </template>
        <div v-if="channel.key === 'onebot' || channel.key === 'qqbot'" class="field-control">
          <span>发送目标</span>
          <AppSelect
            v-model="form[channel.key === 'onebot' ? 'oneBotSendTarget' : 'qqBotSendTarget']"
            :options="sendTargetOptions"
            :disabled="loading || !settingsLoaded || saving || safeModeEnabled"
            aria-label="发送目标"
          />
        </div>
        <div v-if="channel.key === 'qqbot'" class="field-control">
          <span>消息格式</span>
          <AppSelect
            v-model="form.qqBotMessageFormat"
            :options="messageFormatOptions"
            :disabled="loading || !settingsLoaded || saving || safeModeEnabled"
            aria-label="消息格式"
          />
        </div>
        <div class="notification-channel-actions">
          <button
            v-if="channel.key === 'telegram' || channel.key === 'feishu' || channel.key === 'qqbot' || channel.key === 'onebot'"
            type="button"
            class="button button-secondary command-bot-button"
            :class="{ enabled: botEnabled[channel.key] }"
            :disabled="loading || !settingsLoaded || saving || !botsLoaded || safeModeEnabled"
            title="命令机器人设置"
            aria-label="命令机器人设置"
            @click="botDialog = channel.key"
          ><AppIcon name="bot" /></button>
          <button
            class="button button-secondary"
            :class="{ 'is-pending': testingChannel === channel.key }"
            :disabled="loading || !settingsLoaded || saving || testing"
            @click="test(channel.key)"
          >
            {{ testingChannel === channel.key ? '正在发送' : '测试发送' }}
          </button>
          <button v-if="channel.key !== 'windows'" class="button button-primary" :class="{ 'is-pending': savingChannel === channel.key }" :disabled="loading || !settingsLoaded || saving" @click="save(channel.key)">
            {{ savingChannel === channel.key ? '正在保存' : '保存' }}
          </button>
        </div>
      </div>
    </section>
    <CommandBotSettingsModal v-if="botDialog" :key="botDialog" :channel="botDialog" :initial-enabled="botEnabled[botDialog]" @enabled-change="botEnabled[botDialog] = $event" :connection-dirty="connectionDirty" @close="botDialog = undefined" />
  </div>
</template>

<style scoped>
.notification-channel-actions .command-bot-button { margin-right: auto; flex: 0 0 auto; padding: 8px; color: var(--text-muted); }
.notification-channel-actions .command-bot-button.enabled { color: var(--accent-text); }
</style>
