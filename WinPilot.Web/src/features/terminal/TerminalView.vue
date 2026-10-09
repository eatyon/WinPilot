<script setup lang="ts">
import { FitAddon } from '@xterm/addon-fit';
import { Terminal } from '@xterm/xterm';
import '@xterm/xterm/css/xterm.css';
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { showToast } from '../../ui/toast';
import { safeModeEnabled } from '../security/safeMode';
import AppSelect from '../../components/AppSelect.vue';

type Shell = 'powershell' | 'cmd';

interface TerminalTab {
  id: string;
  shell: Shell;
  workingDirectory: string;
  input: string;
  status: 'running' | 'exited';
}

interface TerminalEvent {
  type: string;
  sessionId: string | null;
  stream: string | null;
  data: string;
}

interface TerminalRenderer {
  terminal: Terminal;
  fitAddon: FitAddon;
  columns: number;
  rows: number;
}

const maxSessions = 20;
const tabs = ref<TerminalTab[]>([]);
const activeId = ref('');
const shell = ref<Shell>('powershell');
const workingDirectory = ref('');
const connected = ref(false);
const isCreating = ref(false);
const pendingCreateCount = ref(0);
const tabStrip = ref<HTMLElement | null>(null);
const terminalHosts = new Map<string, HTMLElement>();
const renderers = new Map<string, TerminalRenderer>();
const pendingOutput = new Map<string, string[]>();
const preCreationOutput = new Map<string, string[]>();

let isPageActive = true;
let socket: WebSocket | undefined;
let resizeObserver: ResizeObserver | undefined;

const activeTab = computed(() => tabs.value.find((tab) => tab.id === activeId.value));
const sessionCount = computed(() => tabs.value.length + pendingCreateCount.value);
const isSessionLimitReached = computed(() => sessionCount.value >= maxSessions);

onBeforeUnmount(() => {
  isPageActive = false;
  disconnect();
  resizeObserver?.disconnect();
  resizeObserver = undefined;
  for (const renderer of renderers.values()) renderer.terminal.dispose();
  renderers.clear();
  terminalHosts.clear();
  pendingOutput.clear();
  preCreationOutput.clear();
  tabs.value = [];
});

watch(
  activeId,
  () =>
    void nextTick(() => {
      fitActiveTerminal();
      scrollActiveTabIntoView();
    }),
);

function markDisconnected() {
  connected.value = false;
  isCreating.value = false;
  pendingCreateCount.value = 0;
  preCreationOutput.clear();
  for (const tab of tabs.value) {
    if (tab.status === 'running') {
      tab.status = 'exited';
      writeOutput(tab.id, '\\r\\n[终端连接已关闭]\\r\\n');
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

function connect() {
  const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
  const current = new WebSocket(`${scheme}//${location.host}/api/terminal/ws`);
  socket = current;
  let reportedFailure = false;
  const isCurrent = () => isPageActive && socket === current;
  current.onopen = () => {
    if (!isCurrent()) return;
    if (safeModeEnabled.value) { disconnect(); return; }
    connected.value = true;
    isCreating.value = false;
    sendCreate();
  };
  current.onclose = () => {
    if (!isCurrent()) return;
    if (!reportedFailure && tabs.value.some(tab => tab.status === 'running')) showToast('终端连接已关闭', 'error');
    markDisconnected();
  };
  current.onerror = () => {
    if (!isCurrent() || reportedFailure) return;
    reportedFailure = true;
    showToast('无法连接到 WinPilot 终端', 'error');
  };
  current.onmessage = (event) => {
    if (!isCurrent()) return;
    try { handleEvent(JSON.parse(event.data) as TerminalEvent); }
    catch { showToast('终端消息格式无效', 'error'); }
  };
}

function send(payload: object) {
  if (!isPageActive || safeModeEnabled.value || socket?.readyState !== WebSocket.OPEN) return false;
  try {
    socket.send(JSON.stringify(payload));
    return true;
  } catch { return false; }
}

function createTerminal() {
  if (safeModeEnabled.value) return;
  if (isCreating.value) return;
  if (isSessionLimitReached.value) {
    showToast(`已达到终端数量上限（${maxSessions}）`, 'error');
    return;
  }
  if (!connected.value) {
    isCreating.value = true;
    connect();
    return;
  }
  sendCreate();
}

function sendCreate() {
  if (safeModeEnabled.value || isSessionLimitReached.value) return;
  if (send({ type: 'create', shell: shell.value, workingDirectory: workingDirectory.value || null })) {
    pendingCreateCount.value++;
  }
}

function handleEvent(event: TerminalEvent) {
  if (!isPageActive || !event || typeof event.type !== 'string' || typeof event.data !== 'string') return;
  if (event.type === 'created' && event.sessionId) {
    try {
      const info = JSON.parse(event.data) as { sessionId: string; shell: Shell; workingDirectory: string };
      if (info.sessionId !== event.sessionId || !['cmd', 'powershell'].includes(info.shell) || typeof info.workingDirectory !== 'string') throw new Error();
      if (tabs.value.some(tab => tab.id === info.sessionId)) return;
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
        pendingOutput.set(info.sessionId, output);
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

function setTerminalHost(id: string, element: unknown) {
  if (element instanceof HTMLElement) {
    terminalHosts.set(id, element);
    void nextTick(() => ensureRenderer(id));
  } else {
    const host = terminalHosts.get(id);
    if (host) resizeObserver?.unobserve(host);
    terminalHosts.delete(id);
  }
}

function ensureRenderer(id: string) {
  const host = terminalHosts.get(id);
  if (!isPageActive || !tabs.value.some(tab => tab.id === id) || !host || renderers.has(id)) return;

  const terminal = new Terminal({
    convertEol: false,
    cursorBlink: false,
    disableStdin: true,
    fontFamily: 'Consolas, "Cascadia Mono", monospace',
    fontSize: 15,
    lineHeight: 1.35,
    scrollback: 5000,
    theme: {
      background: '#101720',
      foreground: '#dbe7f3',
      cursor: '#dbe7f3',
      selectionBackground: '#35536f',
    },
  });
  const fitAddon = new FitAddon();
  terminal.loadAddon(fitAddon);
  terminal.open(host);
  renderers.set(id, { terminal, fitAddon, columns: 0, rows: 0 });

  const output = pendingOutput.get(id);
  if (output) {
    for (const chunk of output) terminal.write(chunk);
    pendingOutput.delete(id);
  }

  resizeObserver ??= new ResizeObserver(() => fitActiveTerminal());
  resizeObserver.observe(host);
  if (id === activeId.value) fitActiveTerminal();
}

function writeOutput(id: string, content: string) {
  if (!isPageActive || !tabs.value.some(tab => tab.id === id)) return;
  const renderer = renderers.get(id);
  if (renderer) {
    renderer.terminal.write(content);
    return;
  }
  const output = pendingOutput.get(id) ?? [];
  output.push(content);
  pendingOutput.set(id, output);
  void nextTick(() => ensureRenderer(id));
}

function fitActiveTerminal() {
  const renderer = renderers.get(activeId.value);
  const host = terminalHosts.get(activeId.value);
  if (!renderer || !host || host.clientWidth === 0 || host.clientHeight === 0) return;
  try {
    renderer.fitAddon.fit();
    if (renderer.columns !== renderer.terminal.cols || renderer.rows !== renderer.terminal.rows) {
      renderer.columns = renderer.terminal.cols;
      renderer.rows = renderer.terminal.rows;
      send({ type: 'resize', sessionId: activeId.value, columns: renderer.columns, rows: renderer.rows });
    }
  } catch {}
}

function scrollActiveTabIntoView() {
  tabStrip.value
    ?.querySelector<HTMLButtonElement>('.terminal-tab.active')
    ?.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'smooth' });
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
  send({ type: 'close', sessionId: tab.id });
  const renderer = renderers.get(tab.id);
  if (renderer) {
    renderer.terminal.dispose();
    renderers.delete(tab.id);
  }
  const host = terminalHosts.get(tab.id);
  if (host) resizeObserver?.unobserve(host);
  terminalHosts.delete(tab.id);
  pendingOutput.delete(tab.id);
  preCreationOutput.delete(tab.id);
  tabs.value = tabs.value.filter((item) => item.id !== tab.id);
  if (activeId.value === tab.id) activeId.value = tabs.value.at(-1)?.id ?? '';
}
watch(safeModeEnabled, (enabled) => { if (enabled) disconnect(); }, { flush: 'sync' });
</script>

<template>
  <section
    class="terminal-view"
    :class="{ 'safe-mode-locked': safeModeEnabled, 'safe-mode-blocked': safeModeEnabled }"
    :inert="safeModeEnabled"
  >
    <div class="terminal-toolbar">
      <label class="field-control">
        <span>终端管理员</span>
        <AppSelect
          v-model="shell"
          :options="[
            { value: 'powershell', label: 'PowerShell' },
            { value: 'cmd', label: 'CMD' },
          ]"
        />
      </label>
      <label class="field-control terminal-directory">
        <span>工作目录（可选）</span>
        <input v-model="workingDirectory" placeholder="默认：当前用户目录" @keyup.enter="createTerminal" />
      </label>
      <button
        class="button button-primary"
        type="button"
        :disabled="safeModeEnabled || isCreating || isSessionLimitReached"
        @click="createTerminal"
      >
        {{ isCreating ? '正在创建…' : isSessionLimitReached ? `已达上限（${maxSessions}）` : '新建终端' }}
      </button>
    </div>
    <p v-if="isCreating" class="status-message">正在连接 WinPilot 终端</p>
    <p v-else-if="isSessionLimitReached" class="status-message">
      已达到终端数量上限（{{ maxSessions }}），关闭一个终端后可再次新建
    </p>
    <div ref="tabStrip" class="terminal-tabs">
      <span v-if="!tabs.length" class="terminal-tabs-empty">未创建终端</span>
      <button
        v-for="tab in tabs"
        :key="tab.id"
        class="terminal-tab"
        :class="{ active: tab.id === activeId }"
        type="button"
        @click="activeId = tab.id"
      >
        {{ tab.shell === 'cmd' ? 'CMD' : 'PowerShell' }}
        <small v-if="tab.status === 'exited'">已退出</small>
        <span class="terminal-tab-close" @click.stop="closeTerminal(tab)">×</span>
      </button>
    </div>
    <div v-if="activeTab" class="terminal-panel">
      <div class="terminal-panel-header">
        <strong>{{ activeTab.shell === 'cmd' ? 'CMD' : 'PowerShell' }}</strong>
        <div>
          <button
            class="button button-secondary"
            type="button"
            :disabled="!connected || activeTab.status !== 'running'"
            @click="interruptTerminal(activeTab)"
          >
            中止
          </button>
          <button class="button button-secondary" type="button" @click="closeTerminal(activeTab)">关闭</button>
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
      <form class="terminal-input" @submit.prevent="submitCommand">
        <span>{{ activeTab.status === 'running' ? '>' : '×' }}</span>
        <textarea
          v-model="activeTab.input"
          :disabled="!connected || activeTab.status !== 'running'"
          rows="1"
          autocomplete="off"
          placeholder="输入命令后按 Enter 执行"
          @keydown.enter.exact.prevent="submitCommand"
        />
        <button class="button button-primary" type="submit" :disabled="!connected || activeTab.status !== 'running'">
          执行
        </button>
      </form>
    </div>
    <div v-else class="terminal-empty">新建一个终端以开始执行命令</div>
  </section>
</template>
<style scoped>
.terminal-view { display: grid; height: calc(100vh - 240px); min-height: 680px; grid-template-rows: auto auto minmax(0, 1fr); gap: 12px; }
.terminal-view > .status-message { margin: 0; }
.terminal-toolbar { display: flex; grid-row: 1; align-items: end; gap: 14px; padding: 18px; border: 1px solid var(--border); border-radius: var(--radius-md); background: var(--surface); }
.terminal-toolbar .field-control { flex: 0 0 150px; }
.terminal-toolbar .terminal-directory { flex: 1 1 260px; }
.terminal-toolbar input { width: 100%; }
.terminal-tabs { display: flex; grid-row: 2; min-width: 0; min-height: 48px; gap: 4px; overflow-x: auto; padding: 4px; border: 1px solid var(--border); border-radius: var(--radius-md); background: var(--surface); overscroll-behavior-inline: contain; scroll-behavior: smooth; }
.terminal-tabs:has(.terminal-tabs-empty) { padding: 0; }
.terminal-tabs-empty { display: flex; min-height: 48px; align-items: center; padding: 0 16px; color: var(--text-muted); font-size: 13px; }
.terminal-tab { display: flex; flex: 0 0 auto; min-width: 132px; align-items: center; justify-content: space-between; gap: 9px; padding: 11px 14px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface-subtle); color: var(--text-muted); font-size: 14px; white-space: nowrap; }
.terminal-tab.active { border-color: var(--accent-border); background: var(--accent-bg); color: var(--accent-text); }
.terminal-tab small { color: var(--text-subtle); }
.terminal-tab-close { font-size: 18px; line-height: 12px; }
.terminal-panel { display: grid; grid-row: 3; min-height: 0; grid-template-rows: auto minmax(0, 1fr) auto; overflow: hidden; border: 1px solid #263444; border-radius: var(--radius-md); background: #101720; box-shadow: var(--shadow-float); }
.terminal-panel-header { display: flex; min-width: 0; align-items: center; justify-content: space-between; gap: 12px; padding: 9px 12px; border-bottom: 1px solid #263444; color: #aebdcb; font-size: 12px; }
.terminal-panel-header strong { flex: 0 0 auto; color: #e5edf5; font-size: 14px; }
.terminal-panel-header > div { display: flex; gap: 7px; margin-left: auto; }
.terminal-panel-header .button { min-height: 34px; padding: 6px 12px; }
.terminal-output { min-width: 0; min-height: 0; margin: 0; overflow: hidden; padding: 0; }
.terminal-output-host { width: 100%; height: 100%; box-sizing: border-box; padding: 18px; }
.terminal-input { display: grid; grid-template-columns: auto minmax(0, 1fr) auto; align-items: center; gap: 10px; padding: 14px 16px; border-top: 1px solid #263444; color: #70d6a5; }
.terminal-input textarea { min-width: 0; height: 40px; border: 1px solid #3a4b5f; border-radius: var(--radius-sm); outline: none; background: #182330; color: #fff; padding: 9px; font: 14px/20px Consolas, "Cascadia Mono", monospace; resize: none; }
.terminal-input textarea:focus { border-color: #5b9bd5; }
.terminal-empty { display: grid; grid-row: 3; min-height: 0; place-items: center; border: 1px dashed var(--border-strong); border-radius: var(--radius-md); color: var(--text-muted); }
@media (max-width: 767px) {
  .terminal-view { height: calc(100vh - 170px); min-height: 420px; }
  .terminal-toolbar { display: grid; grid-template-columns: 1fr; align-items: stretch; gap: 10px; padding: 16px; }
  .terminal-toolbar .field-control, .terminal-toolbar .terminal-directory, .terminal-toolbar .button { width: 100%; }
  .terminal-panel-header { align-items: start; }
  .terminal-panel-header > div { flex: 0 0 auto; }
  .terminal-output-host { padding: 14px; }
  .terminal-input { grid-template-columns: auto minmax(0, 1fr); gap: 8px; padding: 12px; }
  .terminal-input .button { grid-column: 1 / -1; width: 100%; }
}
</style>
