import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';
import * as apiErrors from '../support/apiErrors.mjs';

function evaluate(path, require, members = '', extra = {}) {
  const code = loadScriptModule(new URL(path, import.meta.url), members);
  const context = { exports: {}, require, AbortController, DOMException, console, ...extra };
  runInNewContext(code, context);
  return context.exports;
}

function fixture(page, members, props = {}, extra = {}) {
  const calls = [], toasts = [], events = [], emitted = [], renders = [], timers = new Map();
  let mounted, unmount, timerId = 0;
  const client = {
    ...apiErrors,
    apiFetch: (url, init) => url === '/api/settings/notifications/command-bots'
      ? Promise.resolve({ ok: true, json: async () => ({ telegram: true, feishu: false, onebot: false, qqbot: false }) })
      : new Promise((resolve, reject) => calls.push({ url, init, resolve, reject })),
    isAbortError: error => error?.name === 'AbortError',
    userErrorMessage: (error, fallback) => error.message || fallback,
    WinPilotConnectionError: class extends Error {},
  };
  const requests = evaluate('../../src/features/settings/SettingsRequests.ts', () => client);
  const imports = name => {
    if (name === 'vue') return { ...vue, onMounted: callback => { mounted = callback; }, onBeforeUnmount: callback => { unmount = callback; } };
    if (name === 'vue-router') return { useRoute: () => ({ query: {} }), useRouter: () => ({ push() {} }) };
    if (name.endsWith('/SettingsRequests')) return requests;
    if (name.endsWith('/client')) return client;
    if (name.endsWith('/toast')) return { showToast: (message, type = 'error') => { if (message) toasts.push([message, type]); } };
    if (name.endsWith('/markdown')) return { renderMarkdown: source => new Promise((resolve, reject) => renders.push({ source, resolve, reject })) };
    if (name.endsWith('/safeMode')) return { safeModeEnabled: vue.ref(false) };
    return {};
  };
  const scope = vue.effectScope();
  let subject;
  scope.run(() => { subject = evaluate('../../src/features/settings/' + page, imports, members, {
    window: { setTimeout: callback => { timers.set(++timerId, callback); return timerId; }, clearTimeout: id => timers.delete(id), location: { assign() {} } },
    defineProps: () => props,
    defineEmits: () => (...args) => emitted.push(args),
    EventSource: class { constructor(url) { events.push(this); this.url = url; this.closed = false; } close() { this.closed = true; } },
    ...extra,
  }); });
  return { subject, calls, toasts, events, emitted, renders, timers, connectionError: () => new client.WinPilotConnectionError(), mount: () => mounted(), dispose() { unmount(); scope.stop(); } };
}
const response = (value, ok = true) => ({ ok, json: async () => value });
async function loadNotifications(f) {
  const loading = f.mount();
  f.calls[0].resolve(response({ windowsEnabled: false, telegramChatId: 'saved' }));
  await loading;
}

test('notification page reads enabled flags without opening status streams', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'botEnabled,botsLoaded');
  try {
    await loadNotifications(f);
    assert.equal(f.subject.botEnabled.telegram, true);
    assert.equal(f.subject.botsLoaded.value, true);
    assert.equal(f.events.length, 0);
  } finally { f.dispose(); }
});

for (const channel of ['telegram', 'feishu', 'onebot', 'qqbot']) {
  test(channel + ' settings owns one status stream and closes it with the modal', async () => {
    const f = fixture('CommandBotSettingsModal.vue', 'enabled,update,connectionStatus', { channel, initialEnabled: true, connectionDirty: false });
    const loading = f.mount();
    f.calls[0].resolve(response({ enabled: true, prefix: '' }));
    await loading;
    assert.equal(f.events.length, 1);
    assert.equal(f.events[0].url, '/api/notifications/command-bots/' + channel + '/events');
    f.events[0].onmessage({ data: JSON.stringify({ enabled: true, state: 'connected' }) });
    assert.equal(f.subject.connectionStatus.value.tone, 'connected');
    f.subject.enabled.value = true;
    const saving = f.subject.update();
    f.calls[1].resolve(response({ enabled: true, state: 'connecting' }));
    await saving;
    assert.deepEqual(f.emitted.at(-1), ['enabled-change', true]);
    f.dispose();
    assert.equal(f.events[0].closed, true);
    const count = f.emitted.length;
    f.events[0].onmessage({ data: JSON.stringify({ enabled: false, state: 'disabled' }) });
    assert.equal(f.emitted.length, count);
  });
}

test('closing command bot settings during loading cannot create a late status stream', async () => {
  const f = fixture('CommandBotSettingsModal.vue', 'enabled', { channel: 'telegram', initialEnabled: false, connectionDirty: false });
  const loading = f.mount();
  f.dispose();
  f.calls[0].resolve(response({ enabled: true, prefix: '' }));
  await loading;
  assert.equal(f.events.length, 0);
  assert.equal(f.emitted.length, 0);
});

test('status subscriptions follow saved enablement, not unsaved checkbox changes', async () => {
  const f = fixture('CommandBotSettingsModal.vue', 'enabled,update', { channel: 'onebot', initialEnabled: false, connectionDirty: false });
  try {
    const loading = f.mount();
    f.calls[0].resolve(response({ enabled: false, prefix: '' }));
    await loading;
    assert.equal(f.events.length, 0);
    f.subject.enabled.value = true;
    assert.equal(f.events.length, 0);
    const enabling = f.subject.update();
    f.calls[1].resolve(response({ enabled: true, state: 'connecting' }));
    await enabling;
    assert.equal(f.events.length, 1);
    const stream = f.events[0];
    f.subject.enabled.value = false;
    const disabling = f.subject.update();
    f.calls[2].resolve(response({ enabled: false, state: 'disabled' }));
    await disabling;
    assert.equal(stream.closed, true);
    const count = f.emitted.length;
    stream.onmessage({ data: JSON.stringify({ enabled: true, state: 'connected' }) });
    assert.equal(f.emitted.length, count);
    assert.equal(f.events.length, 1);
  } finally { f.dispose(); }
});

test('saving one notification channel preserves other drafts and edits made during save', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'form,save');
  try {
    await loadNotifications(f);
    f.subject.form.telegramChatId = 'draft';
    const saving = f.subject.save('windows');
    f.calls[1].resolve(response({ windowsEnabled: true, telegramChatId: 'saved' }));
    await saving;
    assert.equal(f.subject.form.telegramChatId, 'draft');
    assert.equal(f.subject.form.windowsEnabled, true);
    const telegram = f.subject.save('telegram');
    f.subject.form.telegramChatId = 'newer draft';
    f.calls[2].resolve(response({ telegramChatId: 'draft' }));
    await telegram;
    assert.equal(f.subject.form.telegramChatId, 'newer draft');
  } finally { f.dispose(); }
});

test('notification body arriving after unmount cannot create SSE connections', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'form');
  let finish;
  const loading = f.mount();
  f.calls[0].resolve({ ok: true, json: () => new Promise(resolve => { finish = resolve; }) });
  await setImmediate();
  f.dispose();
  finish({ windowsEnabled: true });
  await loading;
  assert.equal(f.events.length, 0);
  assert.equal(f.subject.form.windowsEnabled, false);
});

test('notification toggle rolls back on failure and duplicate saves are blocked', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'form,save');
  try {
    await loadNotifications(f);
    f.subject.form.windowsEnabled = true;
    const saving = f.subject.save('windows', false);
    await f.subject.save('windows', false);
    assert.equal(f.calls.length, 2);
    f.calls[1].resolve(response({ message: 'failed' }, false));
    await saving;
    assert.equal(f.subject.form.windowsEnabled, false);
  } finally { f.dispose(); }
});

test('security notifications use one channel selector and keep event selection unchanged', async () => {
  const f = fixture('tabs/SecuritySettings.vue', 'notifications,channelOptions,saveNotifications');
  try {
    const loading = f.mount();
    f.calls.find(call => call.url === '/api/settings/security/password').resolve(response({ configured: false }));
    f.calls.find(call => call.url === '/api/settings/security/notifications').resolve(response({
      channel: '', events: ['LoginFailed'],
    }));
    f.calls.find(call => call.url === '/api/notifications/channels').resolve(response([
      { value: '', label: '已启用渠道' }, { value: 'QQBot', label: 'QQBot' },
    ]));
    await loading;
    assert.equal(JSON.stringify(f.subject.channelOptions.value), JSON.stringify([
      { value: '', label: '禁用' }, { value: 'all-enabled', label: '已启用渠道' }, { value: 'qqbot', label: 'QQBot' },
    ]));
    assert.equal(f.subject.notifications.channel, '');
    for (const channel of ['qqbot', 'all-enabled', '']) {
      f.subject.notifications.channel = channel;
      const saving = f.subject.saveNotifications();
      const call = f.calls.at(-1);
      const body = JSON.parse(call.init.body);
      assert.deepEqual(body, { channel, events: ['LoginFailed'] });
      call.resolve(response(body));
      await saving;
    }
  } finally { f.dispose(); }
});

for (const [page, members, save] of [
  ['tabs/SecuritySettings.vue', 'saveNotifications,savePassword', s => Promise.all([s.saveNotifications(), s.savePassword()])],
  ['tabs/AdvancedSettings.vue', 'save', s => s.save(true)],
  ['SettingsView.vue', 'savePort,saveLanAccess,saveStartupMode', s => Promise.all([s.savePort(), s.saveLanAccess(true), s.saveStartupMode('Enabled')])],
]) {
  test(page + ' cannot save default values after a failed load', async () => {
    const f = fixture(page, members);
    try {
      f.mount();
      for (const call of f.calls) call.resolve(response({}, false));
      await setImmediate();
      const count = f.calls.length;
      await save(f.subject);
      assert.equal(f.calls.length, count);
    } finally { f.dispose(); }
  });
}

test('general settings serialize saves and suppress late success notifications', async () => {
  const f = fixture('SettingsView.vue', 'saveLanAccess,savePort');
  f.mount();
  f.calls[0].resolve(response({ port: 9123, lanAccessEnabled: false, startupMode: 'Disabled' }));
  await setImmediate();
  const saving = f.subject.saveLanAccess(true);
  await f.subject.savePort();
  assert.equal(f.calls.length, 2);
  f.dispose();
  f.calls[1].resolve(response({}));
  await saving;
  assert.equal(f.toasts.length, 0);
});

test('late update installation response cannot restart polling after unmount', async () => {
  const f = fixture('tabs/AboutSettings.vue', 'canInstallUpdate,installUpdate');
  f.subject.canInstallUpdate.value = true;
  const install = f.subject.installUpdate();
  f.dispose();
  f.calls[0].resolve(response({}));
  await install;
  assert.equal(f.calls.length, 1);
  assert.equal(f.timers.size, 0);
});

test('update notes use check result without requests and discard stale rendering on close or unmount', async () => {
  const f = fixture('tabs/AboutSettings.vue', 'checkForUpdates,openUpdateNotes,closeUpdateNotes,updateNotesOpen,updateNotesHtml,updateNotes');
  const checking = f.subject.checkForUpdates();
  f.calls[0].resolve(response({ updateAvailable: true, latestVersion: '2.0.0', releaseUrl: 'https://cnb.cool/releases/tag/v2.0.0', releaseNotes: '## 改进' }));
  await checking;
  const first = f.subject.openUpdateNotes();
  assert.equal(f.renders[0].source, '## 改进');
  f.subject.closeUpdateNotes();
  const second = f.subject.openUpdateNotes();
  f.renders[0].resolve('<h2>旧结果</h2>');
  await first;
  assert.equal(f.subject.updateNotesHtml.value, '');
  f.renders[1].resolve('<h2>改进</h2>');
  await second;
  assert.equal(f.subject.updateNotesHtml.value, '<h2>改进</h2>');
  f.subject.closeUpdateNotes();
  assert.equal(f.subject.updateNotesHtml.value, '');
  assert.equal(f.subject.updateNotes.value, '## 改进');
  const third = f.subject.openUpdateNotes();
  f.dispose();
  f.renders[2].resolve('<h2>晚到结果</h2>');
  await third;
  assert.equal(f.subject.updateNotesOpen.value, false);
  assert.equal(f.subject.updateNotesHtml.value, '');
  assert.equal(f.calls.length, 1);
});

test('saving update settings clears notes and empty notes open without rendering', async () => {
  const f = fixture('tabs/AboutSettings.vue', 'checkForUpdates,openUpdateNotes,saveUpdateSettings,updateNotesOpen,updateNotes,updateReleaseUrl');
  try {
    const checking = f.subject.checkForUpdates();
    f.calls[0].resolve(response({ updateAvailable: true, latestVersion: '2.0.0', releaseUrl: 'https://github.com/releases/v2.0.0', releaseNotes: '' }));
    await checking;
    await f.subject.openUpdateNotes();
    assert.equal(f.subject.updateNotesOpen.value, true);
    assert.equal(f.renders.length, 0);
    const saving = f.subject.saveUpdateSettings();
    f.calls[1].resolve(response({ source: 'Cnb', channel: 'Preview' }));
    await saving;
    assert.equal(f.subject.updateNotesOpen.value, false);
    assert.equal(f.subject.updateNotes.value, '');
    assert.equal(f.subject.updateReleaseUrl.value, '');
  } finally { f.dispose(); }
});

test('closing advanced settings cancels saves without late errors or notifications', async () => {
  const f = fixture('tabs/AdvancedSettings.vue', 'save');
  const loading = f.mount();
  f.calls[0].resolve(response({ enabled: false }));
  await loading;
  const saving = f.subject.save(true);
  f.dispose();
  assert.equal(f.calls[1].init.signal.aborted, true);
  f.calls[1].reject(new Error('late failure'));
  await saving;
  assert.equal(f.toasts.length, 0);
});

test('closing about settings suppresses late modal opening and notifications', async () => {
  const f = fixture('tabs/AboutSettings.vue', 'openUpdateSettings,updateSettingsOpen');
  const opening = f.subject.openUpdateSettings();
  f.dispose();
  f.calls[0].resolve(response({ source: 'Cnb', channel: 'Preview' }));
  await opening;
  assert.equal(f.subject.updateSettingsOpen.value, false);
  assert.equal(f.toasts.length, 0);
});

test('update settings show server errors or the page fallback for malformed error bodies', async () => {
  for (const [body, expected] of [
    [null, '保存更新设置失败，请稍后重试'],
    [{ message: '安全模式下不可执行此操作' }, '安全模式下不可执行此操作'],
    [{ errors: { source: ['请选择更新源'] } }, '请选择更新源'],
  ]) {
    const f = fixture('tabs/AboutSettings.vue', 'saveUpdateSettings');
    try {
      const saving = f.subject.saveUpdateSettings();
      f.calls[0].resolve(response(body, false));
      await saving;
      assert.deepEqual(f.toasts, [[expected, 'error']]);
    } finally { f.dispose(); }
  }
});

test('closing about settings suppresses late save failures', async () => {
  const f = fixture('tabs/AboutSettings.vue', 'saveUpdateSettings');
  const saving = f.subject.saveUpdateSettings();
  f.dispose();
  f.calls[0].reject(new Error('late failure'));
  await saving;
  assert.equal(f.toasts.length, 0);
});

test('update status polling schedules only after the preceding request completes', async () => {
  const f = fixture('tabs/AboutSettings.vue', 'canInstallUpdate,installUpdate,installingUpdate,updateMessage,updateMessageType');
  try {
    f.subject.canInstallUpdate.value = true;
    const install = f.subject.installUpdate();
    f.calls[0].resolve(response({}));
    await setImmediate();
    assert.equal(f.calls.length, 2);
    assert.equal(f.timers.size, 0);
    f.calls[1].resolve(response({ state: 'Downloading', downloadedBytes: 5, totalBytes: 10, message: '下载中' }));
    await install;
    assert.equal(f.timers.size, 1);
    const [id, tick] = [...f.timers][0];
    f.timers.delete(id);
    const poll = tick();
    assert.equal(f.calls.length, 3);
    assert.equal(f.timers.size, 0);
    f.calls[2].resolve(response({ state: 'Failed', downloadedBytes: 5, message: '失败' }));
    await poll;
    await setImmediate();
    assert.equal(f.timers.size, 0);
    assert.equal(f.subject.installingUpdate.value, false);
    assert.equal(f.subject.canInstallUpdate.value, false);
    assert.equal(f.subject.updateMessage.value, '失败');
    assert.equal(f.subject.updateMessageType.value, 'error');
  } finally { f.dispose(); }
});

test('install submits the checked version and channel and displays a changed-target rejection', async () => {
  const f = fixture('tabs/AboutSettings.vue', 'checkForUpdates,installUpdate,installingUpdate,canInstallUpdate,updateMessage,updateMessageType');
  try {
    const checking = f.subject.checkForUpdates();
    f.calls[0].resolve(response({ source: 'Cnb', channel: 'Preview', updateAvailable: true, latestVersion: '2.0.0-beta.1', canInstall: true }));
    await checking;
    const install = f.subject.installUpdate();
    assert.equal(f.calls[1].url, '/api/update/install');
    assert.equal(f.calls[1].init.headers['Content-Type'], 'application/json');
    assert.deepEqual(JSON.parse(f.calls[1].init.body), { version: '2.0.0-beta.1', source: 'Cnb', channel: 'Preview' });
    f.calls[1].resolve(response({ message: '更新信息已变化，请重新检查更新' }, false));
    await install;
    assert.equal(f.subject.updateMessage.value, '更新信息已变化，请重新检查更新');
    assert.equal(f.subject.updateMessageType.value, 'error');
    assert.equal(f.subject.installingUpdate.value, false);
    assert.equal(f.subject.canInstallUpdate.value, false);
    assert.equal(f.calls.length, 2);
    assert.equal(f.timers.size, 0);
  } finally { f.dispose(); }
});

for (const actualVersion of ['2.0.0', '1.0.0', '3.0.0']) {
  test('current about page completes update after reconnection: ' + actualVersion, async () => {
    const f = fixture('tabs/AboutSettings.vue', 'version,updateVersion,updateReleaseUrl,updateNotes,canInstallUpdate,installUpdate,installingUpdate,updateMessage,updateMessageType');
    try {
      f.subject.version.value = '1.0.0';
      f.subject.updateVersion.value = '2.0.0';
      f.subject.updateReleaseUrl.value = 'https://github.com/eatyon/WinPilot/releases/tag/v2.0.0';
      f.subject.updateNotes.value = '## 改进';
      f.subject.canInstallUpdate.value = true;
      const install = f.subject.installUpdate();
      f.calls[0].resolve(response({}));
      await setImmediate();
      f.calls[1].reject(f.connectionError());
      await install;
      assert.equal(f.subject.installingUpdate.value, true);
      const [id, tick] = [...f.timers][0];
      f.timers.delete(id);
      tick();
      f.calls[2].resolve(response({ state: 'Idle' }));
      await setImmediate();
      assert.equal(f.calls[3].url, '/api/about');
      f.calls[3].resolve(response({ version: actualVersion }));
      await setImmediate();
      assert.equal(f.subject.version.value, actualVersion === '2.0.0' ? actualVersion : '1.0.0');
      assert.equal(f.subject.updateMessage.value, actualVersion === '2.0.0' ? '更新成功' : '更新失败');
      assert.equal(f.subject.updateMessageType.value, actualVersion === '2.0.0' ? 'success' : 'error');
      assert.equal(f.subject.installingUpdate.value, false);
      assert.equal(f.subject.canInstallUpdate.value, false);
      assert.equal(f.subject.updateNotes.value, '## 改进');
      assert.equal(f.subject.updateReleaseUrl.value, 'https://github.com/eatyon/WinPilot/releases/tag/v2.0.0');
      assert.equal(f.timers.size, 0);
    } finally { f.dispose(); }
  });
}

test('leaving about discards a late version response and does not restore polling on return', async () => {
  const f = fixture('tabs/AboutSettings.vue', 'version,updateVersion,canInstallUpdate,installUpdate,updateMessage');
  f.subject.version.value = '1.0.0';
  f.subject.updateVersion.value = '2.0.0';
  f.subject.canInstallUpdate.value = true;
  const install = f.subject.installUpdate();
  f.calls[0].resolve(response({}));
  await setImmediate();
  f.calls[1].resolve(response({ state: 'Idle' }));
  await setImmediate();
  f.dispose();
  f.calls[2].resolve(response({ version: '2.0.0' }));
  await install;
  assert.equal(f.subject.version.value, '1.0.0');
  assert.notEqual(f.subject.updateMessage.value, '更新成功');
  assert.equal(f.timers.size, 0);
  const next = fixture('tabs/AboutSettings.vue', 'updateMessage');
  try {
    const loading = next.mount();
    next.calls[0].resolve(response({ version: '2.0.0' }));
    await loading;
    assert.equal(next.calls.length, 1);
    assert.equal(next.subject.updateMessage.value, '');
    assert.equal(next.timers.size, 0);
  } finally { next.dispose(); }
});

test('reconnection timeout ends current update tracking without claiming failure', async () => {
  let now = 1000;
  const f = fixture('tabs/AboutSettings.vue', 'installingUpdate,loadUpdateStatus,updateMessage,updateMessageType', {},
    { Date: class extends Date { static now() { return now; } } });
  try {
    f.subject.installingUpdate.value = true;
    const loading = f.subject.loadUpdateStatus();
    f.calls[0].reject(f.connectionError());
    await loading;
    now += 60_000;
    const [id, tick] = [...f.timers][0];
    f.timers.delete(id);
    tick();
    f.calls[1].reject(f.connectionError());
    await setImmediate();
    assert.equal(f.subject.installingUpdate.value, false);
    assert.equal(f.subject.updateMessage.value, '无法确认更新结果');
    assert.equal(f.subject.updateMessageType.value, 'neutral');
    assert.equal(f.timers.size, 0);
  } finally { f.dispose(); }
});
