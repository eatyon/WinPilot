<script setup lang="ts">
import { useModalState } from '../../ui/useModalState';
import AppTooltip from '../../components/AppTooltip.vue';
import { useDelayedLoading } from '../../ui/useDelayedLoading';
import AppPasswordInput from '../../components/AppPasswordInput.vue';
import { NBadge, NFormItem, NButton, NCheckbox, NInput } from 'naive-ui';
import { useId, computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { createSettingsRequests } from './settingsRequests';
import AppModal from '../../components/AppModal.vue';
import AppIcon from '../../components/AppIcon.vue';
import AvailableCommandsModal from './AvailableCommandsModal.vue';
import type { CommandBotChannel, CommandBotConfiguration, CommandGroup } from './commandBotModels';
import { readApiError, isAbortError, userErrorMessage } from '../../api/client';
import { safeModeEnabled } from '../security/safeMode';
import { showToast } from '../../ui/toast';

const formId = useId();

interface CommandBotStatus {
  enabled: boolean;
  state: 'disabled' | 'connecting' | 'connected' | 'reconnecting' | 'failed';
  error?: string;
}
const requests = createSettingsRequests();
const props = defineProps<{
  show?: boolean;
  initialConfiguration: CommandBotConfiguration;
  connectionDirty: boolean;
  channel: CommandBotChannel;
}>();
const channelName = { telegram: 'Telegram', feishu: '飞书', qqbot: 'QQBot', onebot: 'OneBot' }[
  props.channel
];
const channelInText = props.channel === 'feishu' ? channelName : ` ${channelName} `;
const emit = defineEmits<{ close: []; 'after-leave': []; 'enabled-change': [enabled: boolean] }>();
const enabled = ref(props.initialConfiguration.enabled);
const status = ref<CommandBotStatus>({
  enabled: props.initialConfiguration.enabled,
  state: props.initialConfiguration.enabled ? 'connecting' : 'disabled',
});
let events: EventSource | undefined;
const connectionStatus = computed(() => {
  if (!status.value.enabled || status.value.state === 'disabled')
    return { tone: 'disabled', label: '未开启' };
  if (status.value.state === 'connected') return { tone: 'connected', label: '已连接' };
  if (status.value.state === 'failed') return { tone: 'failed', label: '连接失败' };
  return { tone: 'connecting', label: '正在连接' };
});
const saving = ref(false);
const route = `/api/settings/notifications/command-bots/${props.channel}`;
const prefix = ref(props.initialConfiguration.prefix ?? '');
const prefixError = ref('');
watch(prefix, () => { prefixError.value = ''; });
const webSocketUrl = ref(props.initialConfiguration.webSocketUrl ?? '');
const token = ref(props.initialConfiguration.token ?? '');
const showCommandsModal = useModalState(() => false);
const showCommands = showCommandsModal.data;
const commandGroups = ref<CommandGroup[]>([]);
const loadingCommands = ref(false);
const showCommandsLoading = useDelayedLoading(() => loadingCommands.value);
async function openCommands() {
  if (!requests.active || loadingCommands.value || saving.value || showCommandsModal.show) return;
  loadingCommands.value = true;
  try {
    const response = await requests.fetch('/api/notifications/command-bots/commands');
    if (!response.ok) throw new Error('无法读取可用命令');
    commandGroups.value = await requests.json<CommandGroup[]>(response);
    if (props.show === false) return;
    showCommandsModal.open(true);
  } catch (cause) {
    if (requests.active && props.show !== false && !isAbortError(cause))
      showToast(userErrorMessage(cause, '无法读取可用命令'), 'error');
  } finally {
    loadingCommands.value = false;
  }
}
onMounted(() => applyStatus(status.value));

function applyStatus(next: CommandBotStatus) {
  status.value = next;
  emit('enabled-change', next.enabled);
  if (!next.enabled) {
    events?.close();
    events = undefined;
    return;
  }
  if (events || !requests.active) return;
  const stream = new EventSource(`/api/notifications/command-bots/${props.channel}/events`);
  events = stream;
  stream.onmessage = (event) => {
    if (!requests.active || events !== stream) return;
    try {
      applyStatus(JSON.parse(event.data) as CommandBotStatus);
    } catch {}
  };
}

async function update() {
  if (safeModeEnabled.value || saving.value || !requests.active) return;
  if (loadingCommands.value) return;
  if (/\s/.test(prefix.value)) {
    prefixError.value = '命令前缀不能包含空白字符；不使用前缀请留空';
    showToast(prefixError.value, 'error');
    return;
  }
  if (enabled.value && props.connectionDirty) {
    showToast(`连接配置已修改，请先保存${channelInText}渠道配置`, 'error');
    return;
  }
  saving.value = true;
  try {
    const response = await requests.fetch(route, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(
        props.channel === 'onebot'
          ? {
              enabled: enabled.value,
              webSocketUrl: webSocketUrl.value,
              token: token.value,
              prefix: prefix.value,
            }
          : { enabled: enabled.value, prefix: prefix.value },
      ),
    });
    if (!response.ok)
      throw new Error(await readApiError(response, '命令机器人设置保存失败', requests.json));
    const result = await requests.json<CommandBotStatus>(response);
    applyStatus(result);
    showToast('命令机器人设置已保存', 'success');
    emit('close');
  } catch (cause) {
    if (requests.active && !isAbortError(cause))
      showToast(userErrorMessage(cause, '命令机器人设置保存失败'), 'error');
  } finally {
    saving.value = false;
  }
}

onBeforeUnmount(() => {
  requests.dispose();
  events?.close();
});
</script>

<template>
  <AppModal
    :show="show"
    @after-leave="emit('after-leave')"
    title="命令机器人设置"
    :dismissible="!saving"
    :inert="showCommands"
    :dismiss-on-backdrop="false"
    @close="!saving && emit('close')"
  >
    <template #header-actions>
      <AppTooltip text="可用命令">
        <NButton
          quaternary
          class="app-icon-button"
          aria-label="可用命令"
          :loading="showCommandsLoading"
          :aria-busy="loadingCommands"
          :disabled="saving || (loadingCommands && !showCommandsLoading)"
          @click="openCommands"
        >
          <template #icon><AppIcon name="list" /></template>
        </NButton>
      </AppTooltip>
    </template>
    <template #title-extra>
      <AppTooltip :text="connectionStatus.label">
        <NBadge
          dot
          :type="connectionStatus.tone === 'connected' ? 'success' : connectionStatus.tone === 'failed' ? 'error' : 'warning'"
          :color="connectionStatus.tone === 'disabled' ? 'var(--text-subtle)' : undefined"
          :aria-label="connectionStatus.label"
          role="img"
          tabindex="0"
        />
      </AppTooltip>
    </template>
    <div class="command-bot-settings" :inert="loadingCommands">
      <NCheckbox
        v-model:checked="enabled"
        :disabled="saving || safeModeEnabled"
      ><span>启用{{ channelInText }}命令机器人</span></NCheckbox
      >
      <NFormItem
        label="命令前缀"
        :label-props="{ for: `${formId}-field-0` }"
        :show-feedback="Boolean(prefixError)"
        :validation-status="prefixError ? 'error' : undefined"
        :feedback="prefixError"
        class="form-field"
      >
        <NInput
          :input-props="{ id: `${formId}-field-0` }"
          v-model:value="prefix"
          placeholder="留空表示不使用前缀"
          :disabled="saving || safeModeEnabled"
        />
      </NFormItem>
      <p class="field-hint">留空不使用前缀；填写后，仅响应以此前缀开头的命令</p>
      <template v-if="channel === 'onebot'">
        <NFormItem
          label="WebSocket 地址"
          :label-props="{ for: `${formId}-field-1` }"
          :show-feedback="false"
          class="form-field"
        >
          <NInput
            :input-props="{ id: `${formId}-field-1` }"
            v-model:value="webSocketUrl"
            placeholder="例如：ws://127.0.0.1:6700/"
            :disabled="saving || safeModeEnabled"
          />
        </NFormItem>
        <NFormItem
          label="WebSocket Token"
          :label-props="{ for: `${formId}-field-2` }"
          :show-feedback="false"
          class="form-field"
        >
          <AppPasswordInput
            :input-props="{ id: `${formId}-field-2` }"
            v-model:value="token"
            placeholder="填写服务端 Token，无鉴权时留空"
            :disabled="saving || safeModeEnabled"
          />
        </NFormItem>
        <p class="field-hint">
          连接 OneBot 的 WebSocket Server，请使用同时支持事件和 API 的接口（通常为 /）。地址和 Token
          不与 HTTP 通知共用
        </p>
      </template>
      <p class="field-hint">
        <template v-if="channel !== 'onebot'">共用已保存的{{ channelInText }}渠道配置，</template
        >回复发送到原会话
      </p>
      <p v-if="channel === 'feishu'" class="field-hint">
        飞书自建应用需启用机器人，使用长连接，订阅「接收消息」事件、开通消息接收权限并发布应用
      </p>
      <p v-if="channel === 'telegram'" class="field-hint">
        仅接收通知设置中指定聊天 ID 的消息；若为群聊，该群所有成员均可发送命令
      </p>
      <p v-if="channel === 'qqbot'" class="field-hint">
        支持私聊、群聊 @ 和全量群消息；全量接收需 QQ 平台授权
      </p>
      <p v-if="channel === 'onebot' || channel === 'qqbot'" class="field-hint">
        接收范围按通知设置：仅填用户，接收其私聊；仅填群，接收该群所有成员；两者都填，仅接收该用户的私聊和其在指定群的消息；均为空则不接收。与通知发送目标无关
      </p>
    </div>
    <template #actions>
      <NButton attr-type="button" :disabled="saving" @click="emit('close')">取消</NButton>
      <NButton
        attr-type="button"
        :loading="saving"
        :disabled="saving || safeModeEnabled || loadingCommands"
        @click="update()"
        type="primary"
      >{{ saving ? '正在保存' : '保存' }}</NButton
      >
    </template>
  </AppModal>
  <AvailableCommandsModal
    :show="showCommandsModal.show"
    @after-leave="showCommandsModal.afterLeave"
    v-if="showCommands"
    :prefix="prefix"
    :groups="commandGroups"
    @close="showCommandsModal.close()"
  />
</template>

<style scoped>
.command-bot-settings {
  display: grid;
  gap: 12px;
}
.command-bot-settings > p {
  margin: 0;
}
</style>
