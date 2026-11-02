<script setup lang="ts">
import { NFormItem, NAlert, darkTheme, NCard, NButton, NCollapseTransition, NConfigProvider, NInput, NTab, NTabs, NText } from 'naive-ui';
import { useId, computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { showToast } from '../../ui/toast';
import { safeModeEnabled } from '../security/safeMode';
import AppSelect from '../../components/AppSelect.vue';
import { useTheme } from '../../ui/theme';
import { getAccentTokens } from '../../ui/themeColors';
import { createComponentTheme } from '../../ui/componentTheme';
import { useDelayedLoading } from '../../ui/useDelayedLoading';
import { createTerminalRenderers } from './terminalRenderers';

const formId = useId();
const { accentColor } = useTheme();
const terminalTheme = computed(() => createComponentTheme(getAccentTokens(accentColor.value, 'dark'), 'dark'));

type Shell = 'powershell' | 'cmd';

interface TerminalTab {
  id: string;
  shell: Shell;
  workingDirectory: string;
  input: string;
  status: 'running' | 'exited';
}

type TerminalCreateOptions = Pick<TerminalTab, 'shell' | 'workingDirectory'>;

interface TerminalEvent {
  type: string;
  sessionId: string | null;
  stream: string | null;
  data: string;
}

const maxSessions = 20;
const tabs = ref<TerminalTab[]>([]);
const activeId = ref('');
const shell = ref<Shell>('powershell');
const workingDirectory = ref('');
const connected = ref(false);
const isCreating = ref(false);
const showCreateLoading = useDelayedLoading(() => isCreating.value);
const pendingCreateCount = ref(0);
// Shell 输出可能先于 created 事件到达，先按会话暂存，创建标签后再交给渲染器。
const preCreationOutput = new Map<string, string[]>();

let isPageActive = true;
let createGeneration = 0;
let socket: WebSocket | undefined;
const terminalRenderers = createTerminalRenderers({
  isActive: () => isPageActive,
  hasSession: id => tabs.value.some(tab => tab.id === id),
  activeId: () => activeId.value,
  onResize: (id, columns, rows) => {
    if (tabs.value.some(tab => tab.id === id && tab.status === 'running'))
      send({ type: 'resize', sessionId: id, columns, rows });
  },
});
const { setTerminalHost, writeOutput, fitActiveTerminal } = terminalRenderers;

const activeTab = computed(() => tabs.value.find((tab) => tab.id === activeId.value));
const sessionCount = computed(() => tabs.value.length + pendingCreateCount.value);
const isSessionLimitReached = computed(() => sessionCount.value >= maxSessions);

onBeforeUnmount(() => {
  isPageActive = false;
  disconnect();
  terminalRenderers.dispose();
  preCreationOutput.clear();
  tabs.value = [];
});

watch(
  activeId,
  () =>
    void nextTick(() => {
      fitActiveTerminal();
    }),
);

function markDisconnected() {
  createGeneration++;
  connected.value = false;
  isCreating.value = false;
  pendingCreateCount.value = 0;
  preCreationOutput.clear();
  for (const tab of tabs.value) {
    if (tab.status === 'running') {
      tab.status = 'exited';
      writeOutput(tab.id, '\r\n[终端连接已关闭]\r\n');
    }
  }
}

function disconnect() {
  const current = socket;
  socket = undefined;
  if (current) {
    current.onopen = current.onclose = current.onerror = current.onmessage = null;
    current.close();
  }
  markDisconnected();
}

function connect(options: TerminalCreateOptions) {
  const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
  const current = new WebSocket(`${scheme}//${location.host}/api/terminal/ws`);
  socket = current;
  let reportedFailure = false;
  const isCurrent = () => isPageActive && socket === current;
  current.onopen = () => {
    if (!isCurrent()) return;
    if (safeModeEnabled.value) {
      disconnect();
      return;
    }
    connected.value = true;
    isCreating.value = false;
    sendCreate(options);
  };
  current.onclose = () => {
    if (!isCurrent()) return;
    if (!reportedFailure && tabs.value.some((tab) => tab.status === 'running'))
      showToast('终端连接已关闭', 'error');
    markDisconnected();
  };
  current.onerror = () => {
    if (!isCurrent() || reportedFailure) return;
    reportedFailure = true;
    showToast('无法连接到 WinPilot 终端', 'error');
  };
  current.onmessage = (event) => {
    if (!isCurrent()) return;
    try {
      handleEvent(JSON.parse(event.data) as TerminalEvent);
    } catch {
      showToast('终端消息格式无效', 'error');
    }
  };
}

function send(payload: object) {
  if (!isPageActive || safeModeEnabled.value || socket?.readyState !== WebSocket.OPEN) return false;
  try {
    socket.send(JSON.stringify(payload));
    return true;
  } catch {
    return false;
  }
}

async function createTerminal() {
  if (!isPageActive || safeModeEnabled.value || isCreating.value) return;
  if (isSessionLimitReached.value) {
    showToast(`已达到终端数量上限（${maxSessions}）`, 'error');
    return;
  }
  isCreating.value = true;
  const options = { shell: shell.value, workingDirectory: workingDirectory.value };
  const generation = ++createGeneration;
  let connecting = false;
  try {
    await terminalRenderers.prepare();
    if (!isPageActive || safeModeEnabled.value || generation !== createGeneration) return;
    if (!connected.value) {
      connect(options);
      connecting = true;
    } else {
      sendCreate(options);
    }
  } catch {
    if (isPageActive && !safeModeEnabled.value && generation === createGeneration)
      showToast('无法加载终端，请重试', 'error');
  } finally {
    if (!connecting && generation === createGeneration) isCreating.value = false;
  }
}

function sendCreate(options: TerminalCreateOptions) {
  if (safeModeEnabled.value || isSessionLimitReached.value) return;
  if (
    send({ type: 'create', shell: options.shell, workingDirectory: options.workingDirectory || null })
  ) {
    pendingCreateCount.value++;
  }
}

function handleEvent(event: TerminalEvent) {
  if (!isPageActive || !event || typeof event.type !== 'string' || typeof event.data !== 'string')
    return;
  if (event.type === 'created' && event.sessionId) {
    try {
      const info = JSON.parse(event.data) as {
        sessionId: string;
        shell: Shell;
        workingDirectory: string;
      };
      if (
        info.sessionId !== event.sessionId ||
        !['cmd', 'powershell'].includes(info.shell) ||
        typeof info.workingDirectory !== 'string'
      )
        throw new Error();
      if (tabs.value.some((tab) => tab.id === info.sessionId)) return;
      pendingCreateCount.value = Math.max(0, pendingCreateCount.value - 1);
      tabs.value.push({
        id: info.sessionId,
        shell: info.shell,
        workingDirectory: info.workingDirectory,
        input: '',
        status: 'running',
      });
      const output = preCreationOutput.get(info.sessionId);
      if (output) {
        terminalRenderers.bufferOutput(info.sessionId, output);
        preCreationOutput.delete(info.sessionId);
      }
      if (pendingCreateCount.value === 0) preCreationOutput.clear();
      activeId.value = info.sessionId;
    } catch {
      pendingCreateCount.value = Math.max(0, pendingCreateCount.value - 1);
      showToast('终端会话信息无效', 'error');
    }
    return;
  }

  const tab = tabs.value.find((item) => item.id === event.sessionId);
  if (!tab) {
    if (event.type === 'output' && event.sessionId && pendingCreateCount.value > 0) {
      if (!preCreationOutput.has(event.sessionId) && preCreationOutput.size >= maxSessions) return;
      const output = preCreationOutput.get(event.sessionId) ?? [];
      output.push(event.data);
      if (output.length > 64) output.shift();
      preCreationOutput.set(event.sessionId, output);
      return;
    }
    if (event.type === 'error') {
      pendingCreateCount.value = Math.max(0, pendingCreateCount.value - 1);
      if (pendingCreateCount.value === 0) preCreationOutput.clear();
      showToast(event.data, 'error');
    }
    return;
  }

  if (event.type === 'output') {
    writeOutput(tab.id, event.data);
    return;
  }
  if (event.type === 'exited') {
    tab.status = 'exited';
    writeOutput(tab.id, '\r\n[终端已退出]\r\n');
    return;
  }
  if (event.type === 'error') {
    writeOutput(tab.id, `\r\n[错误] ${event.data}\r\n`);
    showToast(event.data, 'error');
  }
}


function closeTab(id: string | number) {
  const tab = tabs.value.find(tab => tab.id === id);
  if (tab) closeTerminal(tab);
}

function handleCommandEnter(event: KeyboardEvent) {
  if (event.isComposing || event.keyCode === 229) return;
  event.preventDefault();
  submitCommand();
}

function submitCommand() {
  const tab = activeTab.value;
  if (!tab || tab.status !== 'running' || !tab.input) return;
  const command = tab.input.replace(/\r\n?|\n/g, '\r');
  if (send({ type: 'input', sessionId: tab.id, data: `${command}\r` })) tab.input = '';
  else showToast('终端连接不可用', 'error');
}

function interruptTerminal(tab: TerminalTab) {
  if (tab.status !== 'running') return;
  send({ type: 'interrupt', sessionId: tab.id });
}

function closeTerminal(tab: TerminalTab) {
  if (tab.status === 'running') send({ type: 'close', sessionId: tab.id });
  terminalRenderers.remove(tab.id);
  preCreationOutput.delete(tab.id);
  tabs.value = tabs.value.filter((item) => item.id !== tab.id);
  if (activeId.value === tab.id) activeId.value = tabs.value.at(-1)?.id ?? '';
}
watch(
  safeModeEnabled,
  (enabled) => {
    if (enabled) disconnect();
  },
  { flush: 'sync' },
);
</script>

<template>
  <section
    class="terminal-view"
    :class="{ 'safe-mode-locked': safeModeEnabled }"
    :inert="safeModeEnabled"
  >
    <div class="terminal-controls">
      <NCard tag="div" content-class="terminal-toolbar">
        <NFormItem
          label="终端管理员"
          :label-props="{ id: (`${formId}-field-0`) + '-label' }"
          :show-feedback="false"
          class="form-field"
        >
          <AppSelect
            :labelled-by="(`${formId}-field-0`) + '-label'"
            :id="`${formId}-field-0`"
            v-model="shell"
            :disabled="isCreating"
            :options="[
              { value: 'powershell', label: 'PowerShell' },
              { value: 'cmd', label: 'CMD' },
            ]"
          />
        </NFormItem>
        <NFormItem
          label="工作目录（可选）"
          :label-props="{ for: `${formId}-field-1` }"
          :show-feedback="false"
          class="form-field terminal-directory"
        >
          <NInput
            :input-props="{ id: `${formId}-field-1` }"
            v-model:value="workingDirectory"
            :disabled="isCreating"
            placeholder="默认：当前用户目录"
            @keyup.enter="createTerminal"
          />
        </NFormItem>
        <NButton
          :loading="showCreateLoading"
          :aria-busy="isCreating"
          attr-type="button"
          :disabled="safeModeEnabled || (isCreating && !showCreateLoading) || isSessionLimitReached"
          @click="createTerminal"
          type="primary"
        >
          {{
            showCreateLoading
              ? '正在创建'
              : isSessionLimitReached
                ? `已达上限（${maxSessions}）`
                : '新建终端'
          }}
        </NButton>
      </NCard>
      <NCollapseTransition :show="isSessionLimitReached" :appear="false">
        <div class="terminal-limit-feedback">
          <NAlert type="info">
            已达到终端数量上限（{{ maxSessions }}），关闭一个终端后可再次新建
          </NAlert>
        </div>
      </NCollapseTransition>
    </div>
    <NCard class="terminal-tabs-card" :content-style="{ padding: 0 }">
      <NTabs v-if="tabs.length" v-model:value="activeId" type="card" closable class="terminal-tabs" @close="closeTab">
        <NTab
          v-for="tab in tabs"
          :key="tab.id"
          :name="tab.id"
          :style="activeId === tab.id ? { backgroundColor: 'color-mix(in srgb, var(--accent-solid) 12%, var(--surface))' } : undefined"
        >
          <NButton
            text
            :class="{ 'terminal-tab-label-exited': tab.status === 'exited' }"
            :type="activeId === tab.id ? 'primary' : 'default'"
            :aria-pressed="activeId === tab.id"
            aria-keyshortcuts="Delete"
            @keydown.delete.prevent="closeTab(tab.id)"
          >
            {{ tab.shell === 'cmd' ? 'CMD' : 'PowerShell' }}
          </NButton>
        </NTab>
      </NTabs>
      <NText v-else :depth="3" class="terminal-tabs-empty">未创建终端</NText>
    </NCard>
    <NConfigProvider
      v-if="activeTab"
      class="terminal-panel"
      :theme="darkTheme"
      :theme-overrides="terminalTheme"
      :style="{ '--scrollbar-color': darkTheme.common.scrollbarColor, '--scrollbar-color-hover': darkTheme.common.scrollbarColorHover }"
    >
      <div class="terminal-panel-header">
        <strong>{{ activeTab.shell === 'cmd' ? 'CMD' : 'PowerShell' }}</strong>
        <div>
          <NButton
            attr-type="button"
            :disabled="!connected || activeTab.status !== 'running'"
            @click="interruptTerminal(activeTab)"
          >
            中止
          </NButton>
          <NButton attr-type="button" @click="closeTerminal(activeTab)">关闭</NButton>
        </div>
      </div>
      <div class="terminal-output">
        <div
          v-for="tab in tabs"
          :key="tab.id"
          v-show="tab.id === activeId"
          class="terminal-output-host"
          :ref="(element) => setTerminalHost(tab.id, element)"
        />
      </div>
      <form class="terminal-input" :class="{ 'is-ended': activeTab.status !== 'running' }" @submit.prevent="submitCommand">
        <span>&gt;</span>
        <NInput
          v-model:value="activeTab.input"
          :disabled="!connected || activeTab.status !== 'running'"
          placeholder="输入命令后按 Enter 发送"
          @keydown.enter.exact="handleCommandEnter"
          :rows="1"
          :resizable="false"
          :input-props="{ autocomplete: 'off' }"
          type="textarea"
        />
        <NButton
          attr-type="submit"
          :disabled="!connected || activeTab.status !== 'running'"
          type="primary"
        >
          发送
        </NButton>
      </form>
    </NConfigProvider>
    <NCard
      v-else
      class="terminal-empty"
      content-class="terminal-empty-content"
      :content-style="{ padding: 0 }"
      :theme-overrides="{ color: 'color-mix(in srgb, var(--canvas) 90%, var(--surface) 10%)', borderColor: 'var(--border-strong)' }"
    >新建一个终端以开始执行命令</NCard>
  </section>
</template>
<style>
.app-workspace:has(.terminal-view) > .n-scrollbar-container > .n-scrollbar-content { height: 100%; }
.page-content:has(.terminal-view) { display: flex; min-height: 100%; flex-direction: column; }
.page-content:has(.terminal-view) > .route-page { display: flex; flex: 1; min-height: 0; }
</style>

<style scoped>
.terminal-view {
  display: flex;
  flex: 1;
  min-width: 0;
  min-height: 0;
  flex-direction: column;
  gap: 12px;
}
.terminal-view > .n-card, .terminal-tabs { flex-shrink: 0; }
.terminal-controls { min-width: 0; flex-shrink: 0; }
.terminal-limit-feedback { padding-top: 12px; }
.terminal-panel, .terminal-empty { flex: 1 0 320px; min-height: 440px; }
.terminal-view :deep(.terminal-toolbar) {
  display: flex;
  align-items: end;
  gap: 14px;
}
.terminal-toolbar .form-field {
  flex: 0 0 150px;
}
.terminal-toolbar .terminal-directory {
  flex: 1 1 260px;
}
.terminal-toolbar input {
  width: 100%;
}
.terminal-tabs {
  min-width: 0;
  width: 100%;
  box-sizing: border-box;
  padding: 4px;
}
.terminal-tabs-empty {
  display: flex;
  min-height: 48px;
  align-items: center;
  padding: 0 16px;
}

.terminal-panel {
  transition: box-shadow .3s cubic-bezier(.4, 0, .2, 1);
  display: grid;
  grid-template-rows: auto minmax(0, 1fr) auto;
  overflow: hidden;
  border: 1px solid v-bind('darkTheme.common.borderColor');
  border-radius: var(--radius-md);
  background: v-bind('darkTheme.common.cardColor');
  box-shadow: var(--shadow-float);
}
.terminal-panel-header {
  display: flex;
  min-width: 0;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 9px 12px;
  border-bottom: 1px solid v-bind('darkTheme.common.borderColor');
  color: v-bind('darkTheme.common.textColor3');
  font-size: 12px;
}
.terminal-panel-header strong {
  flex: 0 0 auto;
  color: v-bind('darkTheme.common.textColor1');
  font-size: 14px;
}
.terminal-panel-header > div {
  display: flex;
  gap: 7px;
  margin-left: auto;
}
.terminal-output {
  min-width: 0;
  min-height: 0;
  margin: 0;
  overflow: hidden;
  padding: 0;
}
.terminal-output-host {
  width: 100%;
  height: 100%;
  box-sizing: border-box;
  padding: 18px;
}
.terminal-output-host :deep(.xterm-cursor-bar) {
  box-shadow: none !important;
}
.terminal-input {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto;
  align-items: center;
  gap: 10px;
  padding: 14px 16px;
  border-top: 1px solid v-bind('darkTheme.common.borderColor');
  color: v-bind('darkTheme.common.successColor');
}
.terminal-input.is-ended {
  color: v-bind('darkTheme.common.errorColor');
}
.terminal-tab-label-exited {
  text-decoration: line-through;
  text-decoration-thickness: 1px;
}
.terminal-input .n-input {
  min-width: 0;
}
.terminal-empty {
  border-style: dashed;
  color: var(--text-muted);
}
.terminal-view :deep(.terminal-empty-content) { display: grid; place-items: center; }
@media (max-width: 767px) {
  .terminal-view :deep(.terminal-toolbar) {
    display: grid;
    grid-template-columns: 1fr;
    align-items: stretch;
    gap: 10px;
  }
  .terminal-toolbar .form-field, .terminal-toolbar .terminal-directory {
    width: 100%;
  }
  .terminal-panel-header {
    align-items: start;
  }
  .terminal-panel-header > div {
    flex: 0 0 auto;
  }
  .terminal-output-host {
    padding: 14px;
  }
  .terminal-input {
    gap: 8px;
    padding: 12px;
  }
}
</style>
