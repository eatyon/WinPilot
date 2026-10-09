<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { createSettingsRequests } from './SettingsRequests';
import AppModal from '../../components/AppModal.vue';
import AppIcon from '../../components/AppIcon.vue';
import AvailableCommandsModal from './AvailableCommandsModal.vue';
import { isAbortError, userErrorMessage } from '../../api/client';
import { safeModeEnabled } from '../security/safeMode';
import { showToast } from '../../ui/toast';

interface CommandBotStatus {
  enabled: boolean;
  state: 'disabled' | 'connecting' | 'connected' | 'reconnecting' | 'failed';
  error?: string;
}
const requests = createSettingsRequests();
const props = defineProps<{ initialEnabled: boolean; connectionDirty: boolean; channel: 'telegram' | 'feishu' | 'qqbot' | 'onebot' }>();
const channelName = { telegram: 'Telegram', feishu: '飞书', qqbot: 'QQBot', onebot: 'OneBot' }[props.channel];
const channelInText = props.channel === 'feishu' ? channelName : ` ${channelName} `;
const emit = defineEmits<{ close: []; 'enabled-change': [enabled: boolean] }>();
const enabled = ref(props.initialEnabled);
const status = ref<CommandBotStatus>({ enabled: props.initialEnabled, state: props.initialEnabled ? 'connecting' : 'disabled' });
let events: EventSource | undefined;
const connectionStatus = computed(() => {
  if (!status.value.enabled || status.value.state === 'disabled') return { tone: 'disabled', label: '未开启' };
  if (status.value.state === 'connected') return { tone: 'connected', label: '已连接' };
  if (status.value.state === 'failed') return { tone: 'failed', label: '连接失败' };
  return { tone: 'connecting', label: '正在连接' };
});
const saving = ref(false);
const route = `/api/settings/command-bots/${props.channel}`;
type CommandBotConfiguration = {
  enabled: boolean;
  prefix?: string;
  webSocketUrl?: string;
  token?: string;
  message?: string;
  detail?: string;
};
const configurationReady = ref(false);
const prefix = ref('');
const webSocketUrl = ref('');
const token = ref('');
const tokenVisible = ref(false);
const showCommands = ref(false);
onMounted(async () => {
  try {
    const response = await requests.fetch(`${route}/configuration`);
    const result = await requests.json<CommandBotConfiguration>(response);
    if (!response.ok) throw new Error(result.message || '无法读取命令机器人设置');
    webSocketUrl.value = result.webSocketUrl ?? '';
    token.value = result.token ?? '';
    enabled.value = result.enabled;
    prefix.value = result.prefix ?? '';
    configurationReady.value = true;
    applyStatus({ enabled: result.enabled, state: result.enabled ? 'connecting' : 'disabled' });
  } catch (cause) {
    if (requests.active && !isAbortError(cause)) showToast(userErrorMessage(cause, '无法读取命令机器人设置'), 'error');
  }
});

function applyStatus(next: CommandBotStatus) {
  status.value = next;
  emit('enabled-change', next.enabled);
  if (!next.enabled) {
    events?.close();
    events = undefined;
    return;
  }
  if (events || !requests.active) return;
  const stream = new EventSource(`${route}/events`);
  events = stream;
  stream.onmessage = (event) => {
    if (!requests.active || events !== stream) return;
    try { applyStatus(JSON.parse(event.data) as CommandBotStatus); }
    catch { }
  };
}

async function update() {
  if (safeModeEnabled.value || saving.value || !requests.active) return;
  if (!configurationReady.value) return;
  if (/\s/.test(prefix.value)) {
    showToast('命令前缀不能包含空白字符；不使用前缀请留空', 'error');
    return;
  }
  if (enabled.value && props.connectionDirty) {
    showToast(`连接配置已修改，请先保存${channelInText}渠道配置`, 'error');
    return;
  }
  saving.value = true;
  try {
    const response = await requests.fetch(props.channel === 'onebot' ? `${route}/configuration` : route, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(props.channel === 'onebot'
        ? { enabled: enabled.value, webSocketUrl: webSocketUrl.value, token: token.value, prefix: prefix.value }
        : { enabled: enabled.value, prefix: prefix.value }),
    });
    const result = await requests.json<CommandBotStatus & { message?: string; detail?: string }>(response);
    if (!response.ok) throw new Error(result.message || result.detail || '命令机器人设置保存失败');
    applyStatus(result);
    showToast('命令机器人设置已保存', 'success');
  } catch (cause) {
    if (requests.active && !isAbortError(cause)) showToast(userErrorMessage(cause, '命令机器人设置保存失败'), 'error');
  } finally { saving.value = false; }
}

onBeforeUnmount(() => { requests.dispose(); events?.close(); });
</script>

<template>
  <AppModal title="命令机器人设置" :inert="showCommands" :dismiss-on-backdrop="false" @close="!saving && emit('close')">
    <template #header-actions>
      <button type="button" class="modal-close command-list-button" title="可用命令" aria-label="可用命令" @click="showCommands = true">
        <AppIcon name="list" />
      </button>
    </template>
    <template #title-extra>
      <span class="command-bot-status-dot" :class="`status-${connectionStatus.tone}`"
        :title="connectionStatus.label" :aria-label="connectionStatus.label" role="img" tabindex="0" />
    </template>
    <div class="command-bot-settings">
      <label class="toggle-control">
        <input v-model="enabled" type="checkbox" :disabled="saving || safeModeEnabled || !configurationReady" />
        <span>启用{{ channelInText }}命令机器人</span>
      </label>
      <label class="field-control">
        <span>命令前缀</span>
        <input v-model="prefix" placeholder="留空表示不使用前缀" :disabled="saving || safeModeEnabled || !configurationReady" />
      </label>
      <p class="field-hint">留空不使用前缀；填写后，仅响应以此前缀开头的命令</p>
      <template v-if="channel === 'onebot'">
        <label class="field-control">
          <span>WebSocket 地址</span>
          <input v-model="webSocketUrl" placeholder="例如：ws://127.0.0.1:6700/" :disabled="saving || safeModeEnabled || !configurationReady" />
        </label>
        <label class="field-control">
          <span>WebSocket Token</span>
          <span class="secret-input">
            <input v-model="token" :type="tokenVisible ? 'text' : 'password'" placeholder="填写服务端 Token，无鉴权时留空"
              :disabled="saving || safeModeEnabled || !configurationReady" />
            <button type="button" class="secret-toggle" :aria-label="tokenVisible ? '隐藏 Token' : '显示 Token'" @click="tokenVisible = !tokenVisible">
              <AppIcon :name="tokenVisible ? 'eye-off' : 'eye'" />
            </button>
          </span>
        </label>
        <p class="field-hint">连接 OneBot 的 WebSocket Server，请使用同时支持事件和 API 的接口（通常为 /）。地址和 Token 不与 HTTP 通知共用</p>
      </template>
      <p class="field-hint"><template v-if="channel !== 'onebot'">共用已保存的{{ channelInText }}渠道配置，</template>回复发送到原会话</p>
      <p v-if="channel === 'feishu'" class="field-hint">飞书自建应用需启用机器人，使用长连接，订阅「接收消息」事件、开通消息接收权限并发布应用</p>
      <p v-if="channel === 'telegram'" class="field-hint">仅接收通知设置中指定聊天 ID 的消息；若为群聊，该群所有成员均可发送命令</p>
      <p v-if="channel === 'qqbot'" class="field-hint">支持私聊、群聊 @ 和全量群消息；全量接收需 QQ 平台授权</p>
      <p v-if="channel === 'onebot' || channel === 'qqbot'" class="field-hint">接收范围按通知设置：仅填用户，接收其私聊；仅填群，接收该群所有成员；两者都填，仅接收该用户的私聊和其在指定群的消息；均为空则不接收。与通知发送目标无关</p>
    </div>
    <template #actions>
      <button type="button" class="button button-secondary" :disabled="saving" @click="emit('close')">取消</button>
      <button type="button" class="button button-primary" :disabled="saving || safeModeEnabled || !configurationReady" @click="update()">{{ saving ? '保存中…' : '保存' }}</button>
    </template>
  </AppModal>
  <AvailableCommandsModal v-if="showCommands" :prefix="prefix" @close="showCommands = false" />
</template>

<style scoped>
.command-bot-settings { display: grid; gap: 12px; }
.command-bot-settings > p { margin: 0; }
.command-list-button .app-icon { width: 18px; height: 18px; }
.command-bot-status-dot { flex: 0 0 10px; width: 10px; height: 10px; border-radius: 50%; background: var(--text-subtle); }
.command-bot-status-dot.status-connected { background: var(--success-text); }
.command-bot-status-dot.status-failed { background: var(--danger-text); }
.command-bot-status-dot.status-connecting { background: var(--warning-text); }
</style>
