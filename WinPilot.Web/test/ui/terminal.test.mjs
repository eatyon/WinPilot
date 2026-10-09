import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';

function fixture() {
  const code = loadScriptModule(new URL('../../src/features/terminal/TerminalView.vue', import.meta.url),
    'tabs, connected, pendingCreateCount, preCreationOutput, pendingOutput, createTerminal, handleEvent, submitCommand, closeTerminal, setTerminalHost');
  const sockets = [], terminals = [], toasts = [], observed = new Set();
  const safeModeEnabled = vue.ref(false);
  let unmount;
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
    constructor() { terminals.push(this); }
    loadAddon() {}
    open() {}
    write(data) { this.output.push(data); }
    dispose() { this.disposed = true; }
  }
  const context = {
    exports: {}, WebSocket: Socket, HTMLElement: Element, location: { protocol: 'http:', host: 'localhost' },
    ResizeObserver: class {
      observe(host) { observed.add(host); }
      unobserve(host) { observed.delete(host); }
      disconnect() { observed.clear(); }
    },
    require: name => {
      if (name === 'vue') return { ...vue, onBeforeUnmount: callback => { unmount = callback; } };
      if (name === '@xterm/xterm') return { Terminal: Renderer };
      if (name === '@xterm/addon-fit') return { FitAddon: class { fit() {} } };
      if (name.endsWith('/toast')) return { showToast: text => toasts.push(text) };
      if (name.endsWith('/safeMode')) return { safeModeEnabled };
      return {};
    },
  };
  const scope = vue.effectScope();
  scope.run(() => runInNewContext(code, context));
  const subject = context.exports;
  const create = async id => {
    subject.createTerminal();
    const socket = sockets.at(-1);
    if (socket.readyState === 0) socket.open();
    subject.handleEvent({ type: 'created', sessionId: id, data: JSON.stringify({ sessionId: id, shell: 'cmd', workingDirectory: 'C:\\' }) });
    const host = new Element();
    subject.setTerminalHost(id, host);
    await vue.nextTick();
    return { socket, host };
  };
  return { subject, sockets, terminals, toasts, observed, safeModeEnabled, create, dispose: () => { unmount(); scope.stop(); } };
}

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
    assert.equal(f.subject.pendingOutput.size, 0);
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
  f.subject.createTerminal();
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
