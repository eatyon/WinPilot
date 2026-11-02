import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';
import { elements, event } from '../support/template.mjs';
import { setImmediate } from 'node:timers/promises';
import * as vue from 'vue';
import { darkTheme } from 'naive-ui';
import * as delayedLoading from '../../src/ui/useDelayedLoading.ts';

test('terminal controls use dark theme overrides regardless of the page mode', () => {
  const source = readFileSync(new URL('../../src/features/terminal/TerminalView.vue', import.meta.url), 'utf8');
  assert.match(source, /createComponentTheme\(getAccentTokens\(accentColor.value, 'dark'\), 'dark'\)/);
  assert.match(source, /:theme="darkTheme"\s+:theme-overrides="terminalTheme"/);
});

test('terminal creation reuses delayed button feedback without inserting a connecting alert', () => {
  const source = readFileSync(new URL('../../src/features/terminal/TerminalView.vue', import.meta.url), 'utf8');
  assert.doesNotMatch(source, /正在连接 WinPilot 终端|<NAlert[^>]*v-if="isCreating"/);
  assert.match(source, /useDelayedLoading\(\(\) => isCreating.value\)/);
  assert.match(source, /:loading="showCreateLoading"/);
  assert.match(source, /:aria-busy="isCreating"/);
  assert.match(source, /isCreating && !showCreateLoading/);
  assert.match(source, /<NCollapseTransition :show="isSessionLimitReached" :appear="false">/);
  assert.match(source, /showToast\('无法连接到 WinPilot 终端', 'error'\)/);
});

test('terminal tab selection applies its background directly to NTab', () => {
  const source = readFileSync(new URL('../../src/features/terminal/TerminalView.vue', import.meta.url), 'utf8');
  assert.match(source, /<NTab\s[^>]*:style="activeId === tab.id \? \{ backgroundColor:/);
  assert.doesNotMatch(source, /:tab-props=/);
});

function fixture({ loadRuntime, loadFont } = {}) {
  const code = loadScriptModule(new URL('../../src/features/terminal/TerminalView.vue', import.meta.url),
    'shell, workingDirectory, tabs, activeId, connected, pendingCreateCount, preCreationOutput, createTerminal, handleEvent, handleCommandEnter, submitCommand, closeTerminal, closeTab, setTerminalHost, fitActiveTerminal');
  const sockets = [], terminals = [], toasts = [], runtimeReads = [], observed = new Set();
  const safeModeEnabled = vue.ref(false);
  let unmount, disposed = false;
  class Socket {
    static OPEN = 1;
    readyState = 0;
    sent = [];
    constructor() { sockets.push(this); }
    send(payload) { this.sent.push(JSON.parse(payload)); }
    close() { this.readyState = 3; this.onclose?.(); }
    open() { this.readyState = 1; this.onopen?.(); }
  }
  class Element { clientWidth = 600; clientHeight = 300; }
  class Renderer {
    cols = 80; rows = 25; disposed = false; output = [];
    textarea = { tabIndex: 0 };
    constructor(options) { this.options = options; terminals.push(this); }
    attachCustomKeyEventHandler(handler) { this.keyHandler = handler; }
    loadAddon() {}
    open() {}
    write(data) { this.output.push(data); }
    dispose() { this.disposed = true; }
  }
  const context = {
    ...(loadFont ? { document: { fonts: { load: loadFont } } } : {}),
    exports: {}, WebSocket: Socket, HTMLElement: Element, location: { protocol: 'http:', host: 'localhost' },
    ResizeObserver: class {
      observe(host) { observed.add(host); }
      unobserve(host) { observed.delete(host); }
      disconnect() { observed.clear(); }
    },
    require: name => {
      if (name.endsWith('/useDelayedLoading')) return delayedLoading;
      if (name === 'naive-ui') return { darkTheme };
      if (name === 'vue') return { ...vue, useId: () => 'terminal-test', onBeforeUnmount: callback => { unmount = callback; } };
      if (name.endsWith('/ui/theme')) return { useTheme: () => ({ accentColor: vue.ref('blue') }) };
      if (name === '@xterm/xterm') {
        runtimeReads.push(name);
        const runtime = loadRuntime ? loadRuntime({ Terminal: Renderer }) : { Terminal: Renderer };
        return runtime instanceof Promise ? Object.assign(runtime, { __esModule: true }) : runtime;
      }
      if (name === '@xterm/addon-fit') return { FitAddon: class { fit() {} } };
      if (name === './terminalRenderers') return rendererModule.exports;
      if (name.endsWith('/toast')) return { showToast: text => toasts.push(text) };
      if (name.endsWith('/safeMode')) return { safeModeEnabled };
      return {};
    },
  };
  const scope = vue.effectScope();
  const rendererModule = { ...context, exports: {} };
  runInNewContext(loadScriptModule(new URL('../../src/features/terminal/terminalRenderers.ts', import.meta.url)), rendererModule);
  scope.run(() => runInNewContext(code, context));
  const subject = context.exports;
  const create = async id => {
    await subject.createTerminal();
    const socket = sockets.at(-1);
    if (socket.readyState === 0) socket.open();
    subject.handleEvent({ type: 'created', sessionId: id, data: JSON.stringify({ sessionId: id, shell: 'cmd', workingDirectory: 'C:\\' }) });
    const host = new Element();
    subject.setTerminalHost(id, host);
    await vue.nextTick();
    return { socket, host };
  };
  return { subject, sockets, terminals, toasts, runtimeReads, observed, safeModeEnabled, create, newHost: () => new Element(), dispose() {
    if (disposed) return;
    disposed = true;
    unmount();
    scope.stop();
  } };
}

test('terminal creation keeps the clicked parameters through runtime loading and connection', async () => {
  let releaseRuntime;
  const f = fixture({ loadRuntime: runtime => new Promise(resolve => { releaseRuntime = () => resolve(runtime); }) });
  try {
    f.subject.shell.value = 'powershell';
    f.subject.workingDirectory.value = 'C:\\first';
    const creating = f.subject.createTerminal();
    await setImmediate();
    f.subject.shell.value = 'cmd';
    f.subject.workingDirectory.value = 'C:\\second';
    await f.subject.createTerminal();
    releaseRuntime();
    await creating;
    assert.equal(f.sockets.length, 1);
    f.subject.workingDirectory.value = 'C:\\third';
    f.sockets[0].open();
    assert.equal(f.sockets[0].sent[0].shell, 'powershell');
    assert.equal(f.sockets[0].sent[0].workingDirectory, 'C:\\first');
  } finally { f.dispose(); }
});

test('terminal runtime loads on creation only and is reused by subsequent sessions', async () => {
  const f = fixture();
  try {
    assert.equal(f.runtimeReads.length, 0);
    assert.equal(f.sockets.length, 0);
    await f.create('first');
    await f.create('second');
    assert.equal(f.runtimeReads.length, 1);
    assert.equal(f.terminals.length, 2);
  } finally { f.dispose(); }
});

test('terminal creation waits for the font alongside runtime loading and reuses readiness', async () => {
  let releaseFont;
  const fontRequests = [];
  const f = fixture({ loadFont: font => {
    fontRequests.push(font);
    return new Promise(resolve => { releaseFont = resolve; });
  } });
  try {
    const creating = f.subject.createTerminal();
    await setImmediate();
    assert.equal(f.runtimeReads.length, 1);
    assert.equal(fontRequests.length, 1);
    assert.equal(f.sockets.length, 0);
    assert.equal(f.terminals.length, 0);
    releaseFont([]);
    await creating;
    await f.create('first');
    await f.create('second');
    assert.equal(fontRequests.length, 1);
    assert.equal(fontRequests[0], `${f.terminals[0].options.fontSize}px "v-mono"`);
    assert.equal(f.terminals[0].options.fontFamily, f.terminals[1].options.fontFamily);
  } finally { f.dispose(); }
});

test('font download failure still permits terminal creation with fallback fonts', async () => {
  const f = fixture({ loadFont: () => Promise.reject(new Error('font unavailable')) });
  try {
    await f.create('fallback');
    assert.equal(f.terminals.length, 1);
    assert.equal(f.toasts.length, 0);
  } finally { f.dispose(); }
});

test('resizing an exited terminal stays local while running sessions still synchronize', async () => {
  const f = fixture();
  try {
    const { socket } = await f.create('resizing');
    socket.sent.length = 0;
    f.terminals[0].cols = 90;
    f.subject.fitActiveTerminal();
    assert.equal(socket.sent.length, 1);
    assert.equal(socket.sent[0].type, 'resize');
    assert.equal(socket.sent[0].columns, 90);
    f.subject.handleEvent({ type: 'exited', sessionId: 'resizing', data: '' });
    socket.sent.length = 0;
    f.terminals[0].cols = 100;
    f.subject.fitActiveTerminal();
    assert.equal(socket.sent.length, 0);
  } finally { f.dispose(); }
});

test('terminal runtime failure permits retry without opening a socket or creating a session', async () => {
  let attempts = 0;
  const f = fixture({ loadRuntime: runtime => {
    if (++attempts === 1) throw new Error('resource unavailable');
    return runtime;
  } });
  try {
    await f.subject.createTerminal();
    assert.equal(f.sockets.length, 0);
    assert.equal(f.subject.tabs.value.length, 0);
    assert.deepEqual(f.toasts, ['无法加载终端，请重试']);
    await f.create('retry');
    assert.equal(f.terminals.length, 1);
  } finally { f.dispose(); }
});

test('pending runtime download prevents duplicate creation and cannot connect after unmount or safe mode', async () => {
  for (const leaving of [true, false]) {
    let release;
    const f = fixture({ loadRuntime: runtime => new Promise(resolve => { release = () => resolve(runtime); }) });
    try {
      const creating = f.subject.createTerminal();
      await setImmediate();
      await f.subject.createTerminal();
      assert.equal(f.runtimeReads.length, 1);
      assert.equal(f.sockets.length, 0);
      if (leaving) f.dispose();
      else f.safeModeEnabled.value = true;
      release();
      await creating;
      assert.equal(f.sockets.length, 0);
      assert.equal(f.terminals.length, 0);
      assert.equal(f.toasts.length, 0);
    } finally { f.dispose(); }
  }
});

test('releasing safe mode during download does not revive an older creation attempt', async () => {
  let release;
  const f = fixture({ loadRuntime: runtime => new Promise(resolve => { release = () => resolve(runtime); }) });
  try {
    const first = f.subject.createTerminal();
    await setImmediate();
    f.safeModeEnabled.value = true;
    await vue.nextTick();
    f.safeModeEnabled.value = false;
    await vue.nextTick();
    const second = f.subject.createTerminal();
    release();
    await Promise.all([first, second]);
    assert.equal(f.sockets.length, 1);
    assert.equal(f.runtimeReads.length, 1);
  } finally { f.dispose(); }
});

test('read-only output skips Tab navigation and lets Tab and Shift+Tab leave mouse focus', async () => {
  const f = fixture();
  try {
    await f.create('navigation');
    const renderer = f.terminals[0];
    assert.equal(renderer.textarea.tabIndex, -1);
    assert.equal(renderer.keyHandler({ key: 'Tab' }), false);
    assert.equal(renderer.keyHandler({ key: 'Tab', shiftKey: true }), false);
    assert.equal(renderer.keyHandler({ key: 'c', ctrlKey: true }), true);
  } finally { f.dispose(); }
});

test('tab closing releases local resources and only requests closure for running sessions', async () => {
  const f = fixture();
  try {
    const { socket } = await f.create('first');
    await f.create('second');
    await f.create('third');
    socket.sent.length = 0;
    f.subject.closeTab('second');
    assert.equal(socket.sent.length, 1);
    assert.equal(socket.sent[0].type, 'close');
    assert.equal(socket.sent[0].sessionId, 'second');
    assert.equal(f.subject.tabs.value.length, 2);
    assert.equal(f.subject.activeId.value, 'third');
    assert.equal(f.terminals[1].disposed, true);
    assert.equal(f.terminals[0].disposed, false);
    assert.equal(f.terminals[2].disposed, false);
    f.subject.handleEvent({ type: 'exited', sessionId: 'third', data: '' });
    f.subject.closeTab('third');
    assert.equal(socket.sent.length, 1);
    assert.equal(f.terminals[2].disposed, true);
    assert.equal(f.subject.activeId.value, 'first');
    f.subject.closeTab('missing');
    assert.equal(f.subject.tabs.value.length, 1);
    f.subject.closeTab('first');
    assert.equal(f.subject.activeId.value, '');
    assert.equal(f.subject.tabs.value.length, 0);
    assert.equal(socket.sent.length, 2);
    assert.deepEqual(f.toasts, []);
  } finally { f.dispose(); }
});

test('read-only output hides focused and unfocused cursors without hiding text', async () => {
  const f = fixture();
  try {
    await f.create('cursor');
    assert.equal(f.terminals[0].options.cursorStyle, 'bar');
    assert.equal(f.terminals[0].options.fontFamily, darkTheme.common.fontFamilyMono);
    assert.equal(f.terminals[0].options.cursorInactiveStyle, 'none');
    const source = readFileSync(new URL('../../src/features/terminal/TerminalView.vue', import.meta.url), 'utf8');
    assert.equal(f.terminals[0].options.theme.background, darkTheme.common.cardColor);
    assert.equal(f.terminals[0].options.theme.foreground, darkTheme.common.textColor2);
    assert.equal(f.terminals[0].options.theme.selectionBackground, darkTheme.common.textColor3);
    assert.match(source, /:deep\(\.xterm-cursor-bar\)\s*\{\s*box-shadow: none !important;/);
    f.subject.handleEvent({ type: 'output', sessionId: 'cursor', data: '保留输出文字' });
    assert.ok(f.terminals[0].output.includes('保留输出文字'));
  } finally { f.dispose(); }
});

test('output failure ends only its tab without disconnecting other sessions', async () => {
  const f = fixture();
  try {
    await f.create('failed');
    await f.create('healthy');
    f.subject.handleEvent({ type: 'error', sessionId: 'failed', data: '终端输出读取失败，正在关闭会话' });
    f.subject.handleEvent({ type: 'exited', sessionId: 'failed', data: '终端会话已结束' });
    assert.equal(f.subject.tabs.value.find(tab => tab.id === 'failed').status, 'exited');
    assert.equal(f.subject.tabs.value.find(tab => tab.id === 'healthy').status, 'running');
    assert.equal(f.subject.connected.value, true);
    assert.deepEqual(f.toasts, ['终端输出读取失败，正在关闭会话']);
  } finally { f.dispose(); }
});

test('composing Enter preserves input while ordinary Enter sends once', async () => {
  const source = readFileSync(new URL('../../src/features/terminal/TerminalView.vue', import.meta.url), 'utf8');
  const inputs = elements(source, node => node.tag === 'NInput' && event(node, 'keydown') === 'handleCommandEnter');
  assert.equal(inputs.length, 1);
  const keydown = inputs[0].props.find(prop => prop.type === 7 && prop.name === 'on' && prop.arg?.content === 'keydown');
  assert.deepEqual(keydown.modifiers.map(modifier => modifier.content), ['enter', 'exact']);
  const f = fixture();
  try {
    const { socket } = await f.create('composition');
    const tab = f.subject.tabs.value[0];
    tab.input = 'echo test';
    let prevented = 0;
    const event = { preventDefault: () => { prevented++; } };
    f.subject.handleCommandEnter({ ...event, isComposing: true, keyCode: 13 });
    f.subject.handleCommandEnter({ ...event, isComposing: false, keyCode: 229 });
    assert.equal(prevented, 0);
    assert.equal(tab.input, 'echo test');
    assert.equal(socket.sent.filter(message => message.type === 'input').length, 0);
    f.subject.handleCommandEnter({ ...event, isComposing: false, keyCode: 13 });
    assert.equal(prevented, 1);
    assert.equal(tab.input, '');
    assert.equal(socket.sent.filter(message => message.type === 'input').length, 1);
    tab.input = 'echo button';
    f.subject.submitCommand();
    assert.equal(socket.sent.filter(message => message.type === 'input').length, 2);
  } finally { f.dispose(); }
});

test('failed sends preserve commands and disconnect marks sessions exited', async () => {
  const f = fixture();
  try {
    const { socket } = await f.create('first');
    f.subject.tabs.value[0].input = 'echo test';
    socket.send = () => { throw new Error(); };
    f.subject.submitCommand();
    assert.equal(f.subject.tabs.value[0].input, 'echo test');
    socket.close();
    assert.equal(f.subject.tabs.value[0].status, 'exited');
    assert.deepEqual(f.terminals[0].output, ['\r\n[终端连接已关闭]\r\n']);
    socket.close();
    assert.equal(f.terminals[0].output.length, 1);
    assert.equal(f.subject.connected.value, false);
    assert.equal(f.subject.pendingCreateCount.value, 0);
  } finally { f.dispose(); }
});

test('closing a tab releases its observer and ignores late output', async () => {
  const f = fixture();
  try {
    const { host } = await f.create('first');
    assert.ok(f.observed.has(host));
    f.subject.closeTerminal(f.subject.tabs.value[0]);
    assert.equal(f.observed.size, 0);
    assert.equal(f.terminals[0].disposed, true);
    f.subject.handleEvent({ type: 'output', sessionId: 'first', data: 'late' });
    assert.equal(f.subject.preCreationOutput.size, 0);
    await vue.nextTick();
    assert.equal(f.terminals.length, 1);
    assert.equal(f.terminals[0].output.includes('late'), false);
  } finally { f.dispose(); }
});

test('stale socket events cannot affect a new connection and invalid JSON is reported', async () => {
  const f = fixture();
  try {
    const { socket: old } = await f.create('first');
    old.close();
    const stale = old.onmessage;
    const { socket } = await f.create('second');
    stale({ data: JSON.stringify({ type: 'output', sessionId: 'second', data: 'wrong' }) });
    assert.ok(!f.terminals[1].output.includes('wrong'));
    socket.onmessage({ data: 'not-json' });
    assert.equal(f.toasts.at(-1), '终端消息格式无效');
  } finally { f.dispose(); }
});

test('safe mode cancels connecting sockets and disposal ignores all late events', async () => {
  const f = fixture();
  await f.subject.createTerminal();
  const socket = f.sockets[0];
  const open = socket.onopen;
  const message = socket.onmessage;
  f.safeModeEnabled.value = true;
  open();
  assert.equal(socket.readyState, 3);
  assert.equal(socket.sent.length, 0);
  f.dispose();
  message({ data: JSON.stringify({ type: 'created', sessionId: 'late', data: '{}' }) });
  assert.equal(f.subject.tabs.value.length, 0);
  assert.equal(f.observed.size, 0);
  assert.equal(f.toasts.length, 0);
});

test('output buffered before the terminal host mounts is flushed in order', async () => {
  const f = fixture();
  try {
    await f.subject.createTerminal();
    f.sockets[0].open();
    f.subject.handleEvent({ type: 'output', sessionId: 'buffered', data: 'first' });
    f.subject.handleEvent({ type: 'created', sessionId: 'buffered', data: JSON.stringify({ sessionId: 'buffered', shell: 'cmd', workingDirectory: 'C:\\' }) });
    f.subject.handleEvent({ type: 'output', sessionId: 'buffered', data: 'second' });
    await vue.nextTick();
    assert.equal(f.terminals.length, 0);
    f.subject.setTerminalHost('buffered', f.newHost());
    await vue.nextTick();
    assert.deepEqual(f.terminals[0].output, ['first', 'second']);
    f.subject.closeTerminal(f.subject.tabs.value.find(tab => tab.id === 'buffered'));
    assert.equal(f.terminals[0].disposed, true);
  } finally { f.dispose(); }
});

test('unmount before a queued renderer mount cannot allocate a terminal or observer', async () => {
  const f = fixture();
  await f.subject.createTerminal();
  f.sockets[0].open();
  f.subject.handleEvent({ type: 'created', sessionId: 'pending', data: JSON.stringify({ sessionId: 'pending', shell: 'cmd', workingDirectory: 'C:\\' }) });
  f.subject.setTerminalHost('pending', f.newHost());
  f.dispose();
  await vue.nextTick();
  assert.equal(f.terminals.length, 0);
  assert.equal(f.observed.size, 0);
});
