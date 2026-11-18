import { readFeatureStyles } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import { modalStateModule } from '../support/modalState.mjs';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';
import * as apiErrors from '../support/apiErrors.mjs';
import * as delayedLoading from '../../src/ui/useDelayedLoading.ts';

test('port save button stays in the input row above validation feedback', () => {
  const source = readFileSync(new URL('../../src/features/settings/tabs/GeneralSettings.vue', import.meta.url), 'utf8');
  assert.match(source, /<NFormItem[^>]*label="Web 服务端口"[\s\S]*?<div class="settings-port-control">[\s\S]*?class="settings-port-save"[\s\S]*?<\/NFormItem>/);
  const css = readFeatureStyles();
  assert.match(css, /\.settings-port-control \{[^}]*align-items: center;/);
});

test('available commands allow the modal default backdrop dismissal', () => {
  const source = readFileSync(new URL('../../src/features/settings/AvailableCommandsModal.vue', import.meta.url), 'utf8');
  assert.doesNotMatch(source, /:dismiss-on-backdrop="false"/);
  assert.match(source, /@close="emit\('close'\)"/);
  assert.match(source, /<NCard[^>]*embedded/);
  assert.match(source, /<NList[^>]*:theme-overrides="\{ color: 'transparent', colorModal: 'transparent' \}"/);
});

test('about links make only their value clickable rather than the whole row', () => {
  const source = readFileSync(new URL('../../src/features/settings/tabs/AboutSettings.vue', import.meta.url), 'utf8');
  assert.equal((source.match(/<div class="about-link-row">/g) ?? []).length, 5);
  for (const text of ['访问 GitHub', '提交 Issue', '查看 Releases', 'AGPL-3.0-only', '查看声明']) {
    assert.ok(source.includes(`>${text}</NButton>`));
  }
  assert.equal((source.match(/<NButton text tag="a"[^>]*target="_blank" rel="noopener noreferrer"/g) ?? []).length, 5);
  assert.doesNotMatch(source, /<a :href="(?:repositoryUrl|issueUrl|releaseUrl|noticesUrl)"/);
  const styles = readFeatureStyles();
  assert.match(styles, /\.about-link-row\s*\{[^}]*min-height: 46px;[^}]*justify-content: space-between/);
  assert.doesNotMatch(styles, /\.about-link-list a/);
});

test('notification cards use public header slots and library transitions without internal overrides', () => {
  const source = readFileSync(new URL('../../src/features/settings/tabs/NotificationSettings.vue', import.meta.url), 'utf8');
  assert.match(source, /<NCard[^>]*:content-style="\{ padding: 0 \}"/);
  assert.match(source, /:header-style="\{ padding: 0 \}"/);
  assert.match(source, /<template #header>/);
  assert.match(source, /<NCollapseTransition :show="Boolean\(expanded\[channel.key\]\)"/);
  assert.match(source, /@click="toggleChannel\(channel.key\)"/);
  assert.match(source, /\.notification-channel-trigger\s*\{[^}]*padding: var\(--n-padding-top\) var\(--n-padding-left\) var\(--n-padding-bottom\);/);
  assert.match(source, /\.notification-channel-content\s*\{[^}]*padding: 0 var\(--n-padding-left\) var\(--n-padding-bottom\);/);
  const styles = readFeatureStyles();
  assert.match(styles, /\.notification-inline-field\s*\{[^}]*width: 100%;[^}]*min-width: 0;/);
  assert.match(source, /<NButton[^>]*class="notification-channel-trigger"/);
  assert.match(source, /:aria-expanded="Boolean\(expanded\[channel.key\]\)"/);
  assert.doesNotMatch(source, /notification-chevron|@keydown|class="notification-channel-header"/);
  assert.doesNotMatch(source, /:deep\(|n-collapse-item|n-button__content|<NCollapseItem/);
});

function evaluate(path, require, members = '', extra = {}) {
  const code = loadScriptModule(new URL(path, import.meta.url), members);
  const context = { exports: {}, require, AbortController, DOMException, console, ...extra };
  runInNewContext(code, context);
  return context.exports;
}

function fixture(page, members, props = {}, extra = {}) {
  if (page === 'tabs/AboutSettings.vue') members = ['updateNotesOpenModal', members].filter(Boolean).join(',');
  if (page === 'tabs/NotificationSettings.vue') members = ['toggleChannel', members].filter(Boolean).join(',');
  const calls = [], toasts = [], events = [], emitted = [], renders = [], timers = new Map();
  const safeModeEnabled = vue.ref(false);
  const route = vue.reactive({ query: { ...extra.query } });
  let mounted, unmount, timerId = 0;
  const client = {
    ...apiErrors,
    apiFetch: (url, init) => url === '/api/settings/notifications/command-bots' && !extra.deferBotReads
      ? Promise.resolve({ ok: true, json: async () => ({ telegram: true, feishu: false, onebot: false, qqbot: false }) })
      : new Promise((resolve, reject) => calls.push({ url, init, resolve, reject })),
    isAbortError: error => error?.name === 'AbortError',
    userErrorMessage: (error, fallback) => error.message || fallback,
    WinPilotConnectionError: class extends Error {},
  };
  const requests = evaluate('../../src/features/settings/settingsRequests.ts', () => client);
  const imports = name => {
    if (name.endsWith('/useDelayedLoading')) return delayedLoading;
    if (name.endsWith('/useModalState')) return modalStateModule;
    if (name.endsWith('/panelEffects')) return { usePanelEffects: () => ({ pageTransitionEnabled: vue.ref(false) }) };
    if (name === 'vue') return { ...vue, useId: () => 'settings-test', onMounted: callback => { mounted = callback; }, onBeforeUnmount: callback => { unmount = callback; } };
    if (name === 'vue-router') return { useRoute: () => route, useRouter: () => ({ push() {} }) };
    if (name.endsWith('/ui/theme')) return { useTheme: () => ({ resolvedTheme: vue.ref('light'), accentColor: vue.ref('blue') }) };
    if (name.endsWith('/settingsRequests')) return requests;
    if (name.endsWith('/client')) return client;
    if (name.endsWith('/toast')) return { showToast: (message, type = 'error') => { if (message) toasts.push([message, type]); } };
    if (name.endsWith('/markdown')) return { renderMarkdown: source => new Promise((resolve, reject) => renders.push({ source, resolve, reject })) };
    if (name.endsWith('/safeMode')) return { safeModeEnabled };
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
  return { subject, calls, toasts, events, emitted, renders, timers, route, safeModeEnabled, connectionError: () => new client.WinPilotConnectionError(), mount: () => mounted?.(), dispose() { unmount(); scope.stop(); } };
}
const response = (value, ok = true) => ({ ok, json: async () => value });

test('command bot entries load before opening, exclude competing actions and allow retry after failure', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'openBotSettings,loadingBotChannel,botDialogModal,botConfiguration,settingsLoaded,botsLoaded,save,test');
  try {
    f.subject.settingsLoaded.value = true;
    f.subject.botsLoaded.value = true;
    const loading = f.subject.openBotSettings('telegram');
    await f.subject.openBotSettings('qqbot');
    await f.subject.save('telegram');
    await f.subject.test('telegram');
    assert.equal(f.calls.length, 1);
    assert.equal(f.subject.loadingBotChannel.value, 'telegram');
    assert.equal(f.subject.botDialogModal.show, false);
    f.calls[0].reject(new Error('读取失败'));
    await loading;
    assert.equal(f.subject.loadingBotChannel.value, '');
    assert.equal(f.toasts.at(-1)[0], '读取失败');
    const retry = f.subject.openBotSettings('telegram');
    await f.subject.openBotSettings('telegram');
    assert.equal(f.calls.length, 2);
    f.calls[1].resolve(response({ enabled: false, prefix: '/' }));
    await retry;
    assert.equal(f.subject.botDialogModal.show, true);
    assert.equal(f.subject.botConfiguration.value.prefix, '/');
  } finally { f.dispose(); }
});
async function loadSecurity(f, configured = true) {
  const loading = f.mount();
  f.calls.find(call => call.url === '/api/settings/security/password').resolve(response({ configured }));
  f.calls.find(call => call.url === '/api/settings/security/notifications').resolve(response({ channel: '', events: [] }));
  f.calls.find(call => call.url === '/api/notifications/channels').resolve(response([]));
  await loading;
}

test('available commands wait for data, block duplicate requests and saving, and recover after failure', async () => {
  const f = fixture('CommandBotSettingsModal.vue', 'openCommands,loadingCommands,showCommandsModal,commandGroups,update', {
    channel: 'telegram', initialConfiguration: { enabled: false }, connectionDirty: false,
  });
  try {
    f.mount();
    const first = f.subject.openCommands();
    await f.subject.openCommands();
    await f.subject.update();
    assert.equal(f.calls.length, 1);
    assert.equal(f.subject.showCommandsModal.show, false);
    f.calls[0].reject(new Error('读取失败'));
    await first;
    assert.equal(f.subject.loadingCommands.value, false);
    assert.equal(f.subject.showCommandsModal.show, false);
    assert.equal(f.toasts.at(-1)[0], '读取失败');
    const retry = f.subject.openCommands();
    f.calls[1].resolve(response([{ name: '命令', commands: [{ usage: 'help', description: '说明' }] }]));
    await retry;
    assert.equal(f.subject.showCommandsModal.show, true);
    assert.equal(f.subject.commandGroups.value[0].commands[0].usage, 'help');
  } finally { f.dispose(); }
});

test('closing bot settings during command loading aborts without opening a late modal', async () => {
  const f = fixture('CommandBotSettingsModal.vue', 'openCommands,showCommandsModal', {
    channel: 'telegram', initialConfiguration: { enabled: false }, connectionDirty: false,
  });
  f.mount();
  const loading = f.subject.openCommands();
  f.dispose();
  assert.equal(f.calls[0].init.signal.aborted, true);
  f.calls[0].resolve(response([]));
  await loading;
  assert.equal(f.subject.showCommandsModal.show, false);
  assert.equal(f.toasts.length, 0);
});

test('a command response arriving during the parent leave transition cannot open another modal', async () => {
  const props = vue.reactive({ channel: 'telegram', initialConfiguration: { enabled: false }, connectionDirty: false, show: true });
  const f = fixture('CommandBotSettingsModal.vue', 'openCommands,showCommandsModal', props);
  try {
    f.mount();
    const loading = f.subject.openCommands();
    props.show = false;
    f.calls[0].resolve(response([]));
    await loading;
    assert.equal(f.subject.showCommandsModal.show, false);
    assert.equal(f.toasts.length, 0);
  } finally { f.dispose(); }
});

test('loading update settings excludes checking for updates until completion', async () => {
  const f = fixture('tabs/AboutSettings.vue', 'openUpdateSettings,checkForUpdates,updateSettingsOpenModal');
  try {
    const opening = f.subject.openUpdateSettings();
    await f.subject.checkForUpdates();
    assert.equal(f.calls.length, 1);
    assert.equal(f.subject.updateSettingsOpenModal.show, false);
    f.calls[0].resolve(response({ source: 'GitHub', channel: 'Stable' }));
    await opening;
    assert.equal(f.subject.updateSettingsOpenModal.show, true);
  } finally { f.dispose(); }
});

test('security password validation uses field feedback without parsing error wording or changing the request', async () => {
  for (const [password, confirmation, field] of [
    ['short', 'short', 'newPassword'], [' leading', ' leading', 'newPassword'],
    ['valid-password', 'different', 'confirmation'], ['valid-password', 'valid-password', 'currentPassword'],
  ]) {
    const f = fixture('tabs/SecuritySettings.vue', 'savePassword,currentPassword,newPassword,confirmation,passwordFeedback,passwordFieldError,error');
    try {
      await loadSecurity(f);
      f.subject.currentPassword.value = 'fake-old-password';
      f.subject.newPassword.value = password;
      f.subject.confirmation.value = confirmation;
      const pending = f.subject.savePassword();
      const call = f.calls.at(-1);
      assert.deepEqual(JSON.parse(call.init.body), { currentPassword: 'fake-old-password', newPassword: password, confirmation });
      call.resolve({ ...response({ errors: { password: ['服务端校验信息'] } }, false), status: 400 });
      await pending;
      assert.equal(f.subject.passwordFieldError(field), '服务端校验信息');
      assert.equal(f.subject.error.value, '');
      f.subject.confirmation.value += 'edited';
      assert.equal(f.subject.passwordFeedback.value, undefined);
    } finally { f.dispose(); }
  }
});

test('security write failures use toast and late validation cannot overwrite edited values', async () => {
  const f = fixture('tabs/SecuritySettings.vue', 'savePassword,newPassword,confirmation,passwordFeedback,error');
  try {
    await loadSecurity(f);
    f.subject.newPassword.value = f.subject.confirmation.value = 'fake-password';
    const pending = f.subject.savePassword();
    f.calls.at(-1).resolve({ ...response({ detail: '无法保存密码，请检查数据目录权限' }, false), status: 500 });
    await pending;
    assert.equal(f.subject.error.value, '');
    assert.deepEqual(f.toasts, [['无法保存密码，请检查数据目录权限', 'error']]);
    assert.equal(f.subject.passwordFeedback.value, undefined);
    const retry = f.subject.savePassword();
    f.subject.newPassword.value = 'edited-password';
    f.calls.at(-1).resolve({ ...response({ errors: { password: ['旧校验结果'] } }, false), status: 400 });
    await retry;
    assert.equal(f.subject.error.value, '');
    assert.equal(f.subject.passwordFeedback.value, undefined);
  } finally { f.dispose(); }
});
async function loadNotifications(f) {
  const loading = f.subject.toggleChannel('telegram');
  f.calls[0].resolve(response({ telegramChatId: 'saved' }));
  await loading;
}

test('Telegram proxy defaults to port 7890 when no saved address is returned', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'form');
  try {
    assert.equal(f.subject.form.telegramProxyUrl, 'http://127.0.0.1:7890');
    await loadNotifications(f);
    assert.equal(f.subject.form.telegramProxyUrl, 'http://127.0.0.1:7890');
  } finally { f.dispose(); }
});

test('Telegram proxy preserves a saved custom address when loaded and saved', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'form,save');
  try {
    const loading = f.subject.toggleChannel('telegram');
    f.calls[0].resolve(response({ telegramProxyUrl: 'http://localhost:10809' }));
    await loading;
    assert.equal(f.subject.form.telegramProxyUrl, 'http://localhost:10809');
    const saving = f.subject.save('telegram');
    assert.equal(JSON.parse(f.calls[1].init.body).telegramProxyUrl, 'http://localhost:10809');
    f.calls[1].resolve(response({ telegramProxyUrl: 'http://localhost:10809' }));
    await saving;
    assert.equal(f.subject.form.telegramProxyUrl, 'http://localhost:10809');
  } finally { f.dispose(); }
});

test('safe notification page keeps channel headings without loading configuration or opening streams', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'channels,form,loading,settingsLoaded,botsLoaded');
  try {
    f.safeModeEnabled.value = true;
    await f.mount();
    await f.subject.toggleChannel('telegram');
    assert.equal(f.subject.channels.length, 7);
    assert.equal(f.subject.loading.value, false);
    assert.equal(f.subject.settingsLoaded.value, false);
    assert.equal(f.subject.botsLoaded.value, false);
    assert.equal(f.subject.form.telegramBotToken, '');
    assert.equal(f.calls.length, 0);
    assert.equal(f.events.length, 0);
    assert.deepEqual(f.toasts, []);
  } finally { f.dispose(); }
});

test('notification marks the clicked header busy and expands only after loading, then reuses configuration', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'expanded,loading,loadingChannel');
  try {
    await f.mount();
    assert.equal(f.calls.length, 0);
    const opening = f.subject.toggleChannel('telegram');
    assert.equal(f.calls.length, 1);
    assert.equal(f.subject.expanded.telegram, false);
    assert.equal(f.subject.loadingChannel.value, 'telegram');
    assert.equal(f.subject.loading.value, true);
    await f.subject.toggleChannel('telegram');
    assert.equal(f.subject.expanded.telegram, false);
    assert.equal(f.calls.length, 1);
    f.calls[0].resolve(response({ telegramChatId: 'saved' }));
    await opening;
    assert.equal(f.subject.expanded.telegram, true);
    assert.equal(f.subject.loadingChannel.value, '');
    await f.subject.toggleChannel('telegram');
    assert.equal(f.subject.expanded.telegram, false);
    await f.subject.toggleChannel('webhook');
    await f.subject.toggleChannel('telegram');
    assert.equal(f.subject.expanded.webhook, true);
    assert.equal(f.subject.expanded.telegram, true);
    assert.equal(f.calls.length, 1);
  } finally { f.dispose(); }
});

test('failed notification loading keeps the card collapsed and clears the busy header for retry', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'expanded,loading,error,loadingChannel');
  try {
    const opening = f.subject.toggleChannel('telegram');
    f.calls[0].resolve(response({ message: '无法读取' }, false));
    await opening;
    assert.equal(f.subject.expanded.telegram, false);
    assert.equal(f.subject.loadingChannel.value, '');
    assert.equal(f.subject.loading.value, false);
    assert.equal(f.subject.error.value, '无法读取');
    const retry = f.subject.toggleChannel('telegram');
    f.calls[1].resolve(response({ telegramChatId: 'saved' }));
    await retry;
    assert.equal(f.subject.expanded.telegram, true);
    assert.equal(f.subject.error.value, '');
  } finally { f.dispose(); }
});

test('notification configuration and bot switches load in parallel without duplicate requests', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'expanded,loading,botsLoaded,settingsLoaded,loadSettings', {}, { deferBotReads: true });
  try {
    const opening = f.subject.toggleChannel('telegram');
    assert.deepEqual(f.calls.map(call => call.url), [
      '/api/settings/notifications', '/api/settings/notifications/command-bots',
    ]);
    assert.equal(f.subject.expanded.telegram, false);
    await f.subject.toggleChannel('webhook');
    assert.equal(f.subject.expanded.webhook, false);
    await f.subject.loadSettings();
    assert.equal(f.calls.length, 2);
    f.calls[1].resolve(response({ telegram: true }));
    await setImmediate();
    assert.equal(f.subject.loading.value, true);
    f.calls[0].resolve(response({ telegramChatId: 'saved' }));
    await opening;
    assert.equal(f.subject.settingsLoaded.value, true);
    assert.equal(f.subject.botsLoaded.value, true);
    assert.equal(f.subject.loading.value, false);
  } finally { f.dispose(); }
});

test('notification page reads command bot enablement without opening status streams', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'botEnabled,botsLoaded');
  try {
    await loadNotifications(f);
    assert.equal(f.subject.botEnabled.telegram, true);
    assert.equal(f.subject.botsLoaded.value, true);
    assert.equal(f.events.length, 0);
  } finally { f.dispose(); }
});

test('unmounting notifications aborts pending reads and ignores private late responses', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'form,expanded,loadingChannel,loadSettings');
  try {
    const loading = f.subject.toggleChannel('telegram');
    f.dispose();
    assert.equal(f.calls[0].init.signal.aborted, true);
    f.calls[0].resolve(response({ telegramBotToken: 'late-read' }));
    await loading;
    assert.equal(f.subject.form.telegramBotToken, '');
    assert.equal(f.subject.expanded.telegram, false);
    assert.equal(f.subject.loadingChannel.value, '');
    await f.subject.loadSettings();
    assert.equal(f.calls.length, 1);
    assert.equal(f.events.length, 0);
    assert.equal(f.toasts.length, 0);
  } finally { f.dispose(); }
});

test('unmounting notifications aborts saves without applying late configuration', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'form,save');
  try {
    await loadNotifications(f);
    f.subject.form.telegramBotToken = 'draft';
    const saving = f.subject.save('telegram');
    f.dispose();
    assert.equal(f.calls[1].init.signal.aborted, true);
    f.calls[1].resolve(response({ telegramBotToken: 'late-save' }));
    await saving;
    assert.equal(f.subject.form.telegramBotToken, 'draft');
    assert.equal(f.toasts.length, 0);
  } finally { f.dispose(); }
});

test('cancelling QQBot acquisition clears its status and ignores late failures', async () => {
  for (const target of ['user', 'group']) {
    const f = fixture('tabs/NotificationSettings.vue', 'bindQQBot,binding,qqbotOpenIdStatus');
    try {
      await loadNotifications(f);
      const pending = f.subject.bindQQBot(target);
      await f.subject.bindQQBot(target);
      assert.equal(f.calls[1].init.signal.aborted, true);
      assert.equal(f.subject.binding.value, '');
      assert.equal(f.subject.qqbotOpenIdStatus[target], null);
      f.calls[1].reject(new Error('late connection failure'));
      await pending;
      assert.equal(f.subject.qqbotOpenIdStatus[target], null);
      assert.equal(f.toasts.length, 0);
    } finally { f.dispose(); }
  }
});

test('unmounting notifications cancels OpenID acquisition and ignores its late failure', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'bindQQBot,binding,qqbotOpenIdStatus');
  try {
    await loadNotifications(f);
    const binding = f.subject.bindQQBot('user');
    assert.equal(f.calls[1].url, '/api/notifications/qqbot/bind-user');
    const status = f.subject.qqbotOpenIdStatus.user;
    f.dispose();
    assert.equal(f.calls[1].init.signal.aborted, true);
    f.calls[1].reject(new Error('late connection failure'));
    await binding;
    assert.equal(f.subject.binding.value, '');
    assert.equal(f.subject.qqbotOpenIdStatus.user, status);
    assert.equal(f.toasts.length, 0);
  } finally { f.dispose(); }
});

for (const channel of ['telegram', 'feishu', 'onebot', 'qqbot']) {
  test(channel + ' settings owns one status stream and closes it with the modal', async () => {
    const f = fixture('CommandBotSettingsModal.vue', 'enabled,update,connectionStatus', { channel, initialConfiguration: { enabled: true, prefix: '' }, connectionDirty: false });
    f.mount();
    assert.equal(f.calls.length, 0);
    assert.equal(f.events.length, 1);
    assert.equal(f.events[0].url, '/api/notifications/command-bots/' + channel + '/events');
    f.events[0].onmessage({ data: JSON.stringify({ enabled: true, state: 'connected' }) });
    assert.equal(f.subject.connectionStatus.value.tone, 'connected');
    f.subject.enabled.value = true;
    const failedSave = f.subject.update();
    f.calls[0].resolve(response({ detail: '保存失败' }, false));
    await failedSave;
    assert.equal(f.emitted.some(([name]) => name === 'close'), false);
    const saving = f.subject.update();
    f.calls[1].resolve(response({ enabled: true, state: 'connecting' }));
    await saving;
    assert.deepEqual(f.emitted.slice(-2), [['enabled-change', true], ['close']]);
    f.dispose();
    assert.equal(f.events[0].closed, true);
    const count = f.emitted.length;
    f.events[0].onmessage({ data: JSON.stringify({ enabled: false, state: 'disabled' }) });
    assert.equal(f.emitted.length, count);
  });
}

test('leaving notifications during bot settings loading cannot open a late modal', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'openBotSettings,settingsLoaded,botsLoaded,botDialogModal');
  f.subject.settingsLoaded.value = true;
  f.subject.botsLoaded.value = true;
  const loading = f.subject.openBotSettings('telegram');
  f.dispose();
  f.calls[0].resolve(response({ enabled: true, prefix: '' }));
  await loading;
  assert.equal(f.events.length, 0);
  assert.equal(f.subject.botDialogModal.show, false);
});

test('status subscriptions follow saved enablement, not unsaved checkbox changes', async () => {
  const f = fixture('CommandBotSettingsModal.vue', 'enabled,update', { channel: 'onebot', initialConfiguration: { enabled: false, prefix: '' }, connectionDirty: false });
  try {
    f.mount();
    assert.equal(f.events.length, 0);
    f.subject.enabled.value = true;
    assert.equal(f.events.length, 0);
    const enabling = f.subject.update();
    f.calls[0].resolve(response({ enabled: true, state: 'connecting' }));
    await enabling;
    assert.equal(f.events.length, 1);
    const stream = f.events[0];
    f.subject.enabled.value = false;
    const disabling = f.subject.update();
    f.calls[1].resolve(response({ enabled: false, state: 'disabled' }));
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
    const saving = f.subject.save('webhook');
    f.calls[1].resolve(response({ webhookUrl: 'saved-url', telegramChatId: 'saved' }));
    await saving;
    assert.equal(f.subject.form.telegramChatId, 'draft');
    assert.equal(f.subject.form.webhookUrl, 'saved-url');
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
  const loading = f.subject.toggleChannel('telegram');
  f.calls[0].resolve({ ok: true, json: () => new Promise(resolve => { finish = resolve; }) });
  await setImmediate();
  f.dispose();
  finish({ webhookUrl: 'late-url' });
  await loading;
  assert.equal(f.events.length, 0);
  assert.equal(f.subject.form.webhookUrl, '');
});

test('notification save failure preserves the draft and duplicate saves are blocked', async () => {
  const f = fixture('tabs/NotificationSettings.vue', 'form,save,savingChannel');
  try {
    await loadNotifications(f);
    f.subject.form.webhookUrl = 'draft-url';
    const saving = f.subject.save('webhook');
    assert.equal(f.subject.savingChannel.value, 'webhook');
    await f.subject.save('webhook');
    assert.equal(f.calls.length, 2);
    f.calls[1].resolve(response({ message: 'failed' }, false));
    await saving;
    assert.equal(f.subject.form.webhookUrl, 'draft-url');
    assert.equal(f.subject.savingChannel.value, '');
    assert.equal(f.toasts.length, 1);
  } finally { f.dispose(); }
});

test('notification tests identify only the active channel and clear it after success or failure', async () => {
  for (const succeeds of [true, false]) {
    const f = fixture('tabs/NotificationSettings.vue', 'test,testingChannel');
    try {
      await loadNotifications(f);
      const sending = f.subject.test('telegram');
      assert.equal(f.subject.testingChannel.value, 'telegram');
      await f.subject.test('webhook');
      assert.equal(f.calls.length, 2);
      assert.equal(f.subject.testingChannel.value, 'telegram');
      if (succeeds) f.calls[1].resolve(response([]));
      else f.calls[1].reject(new Error('send failed'));
      await sending;
      assert.equal(f.subject.testingChannel.value, '');
    } finally { f.dispose(); }
  }
});

test('security event display shares the selection count without changing password event storage', () => {
  const f = fixture('tabs/SecuritySettings.vue', 'notifications,selectedEvents,eventsLabel,toggleEvent');
  try {
    const s = f.subject;
    assert.equal(s.eventsLabel.value, '请选择事件');
    s.notifications.events = ['PasswordChanged', 'LoginFailed'];
    assert.deepEqual(Array.from(s.selectedEvents.value), ['PasswordSet', 'LoginFailed']);
    assert.equal(s.eventsLabel.value, '2 个事件');
    s.toggleEvent('PasswordSet', true);
    assert.equal(s.eventsLabel.value, '2 个事件');
    assert.deepEqual(Array.from(s.notifications.events), ['PasswordChanged', 'LoginFailed', 'PasswordSet']);
    s.toggleEvent('PasswordSet', false);
    assert.deepEqual(Array.from(s.notifications.events), ['LoginFailed']);
    assert.deepEqual(Array.from(s.selectedEvents.value), ['LoginFailed']);
    assert.equal(s.eventsLabel.value, '1 个事件');
    s.toggleEvent('LoginFailed', false);
    assert.equal(s.eventsLabel.value, '请选择事件');
    s.toggleEvent('SessionInvalidated', true);
    assert.deepEqual(Array.from(s.selectedEvents.value), ['SessionInvalidated']);
    assert.equal(s.eventsLabel.value, '1 个事件');
    s.toggleEvent('SessionInvalidated', false);
    assert.deepEqual(Array.from(s.notifications.events), []);
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
      { value: 'QQBot', label: 'QQBot' },
    ]));
    await loading;
    assert.equal(JSON.stringify(f.subject.channelOptions.value), JSON.stringify([
      { value: '', label: '禁用' }, { value: 'qqbot', label: 'QQBot' },
    ]));
    assert.equal(f.subject.notifications.channel, '');
    for (const channel of ['qqbot', '']) {
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
  const queued = f.subject.savePort();
  assert.equal(f.calls.length, 2);
  f.dispose();
  f.calls[1].resolve(response({}));
  await Promise.all([saving, queued]);
  assert.equal(f.toasts.length, 0);
});

async function generalSettingsFixture() {
  const f = fixture('SettingsView.vue', 'savePort,saveLanAccess,saveStartupMode,port,lanAccessEnabled,startupMode,savingKey,errorMessage');
  await f.mount();
  f.calls[0].resolve(response({ port: 9123, lanAccessEnabled: false, startupMode: 'Disabled' }));
  await setImmediate();
  return f;
}

test('general saves queue different fields without changing unrelated values or save states', async () => {
  const f = await generalSettingsFixture();
  try {
    f.subject.port.value = '12345';
    const lan = f.subject.saveLanAccess(true);
    const port = f.subject.savePort();
    const startup = f.subject.saveStartupMode('Enabled');
    assert.equal(f.calls.length, 2);
    assert.equal(f.subject.savingKey.value, 'lanAccess');
    f.calls[1].resolve(response({}));
    await setImmediate();
    assert.equal(f.subject.savingKey.value, 'port');
    assert.deepEqual(JSON.parse(f.calls[2].init.body), { port: 12345 });
    f.calls[2].resolve(response({ port: 12345, lanAccessEnabled: false, startupMode: 'Disabled' }));
    await setImmediate();
    assert.equal(f.subject.lanAccessEnabled.value, true);
    assert.equal(f.subject.startupMode.value, 'Enabled');
    assert.equal(f.subject.savingKey.value, 'startupMode');
    f.calls[3].resolve(response({ startupMode: 'Enabled' }));
    await Promise.all([lan, port, startup]);
    assert.equal(f.subject.savingKey.value, '');
  } finally { f.dispose(); }
});

test('queued saves coalesce to the latest field value and a failure does not block the next field', async () => {
  const f = await generalSettingsFixture();
  try {
    const port = f.subject.savePort();
    const enabled = f.subject.saveStartupMode('Enabled');
    const delayed = f.subject.saveStartupMode('Delayed');
    const lan = f.subject.saveLanAccess(true);
    f.calls[1].resolve(response({ port: 9123 }));
    await setImmediate();
    assert.deepEqual(JSON.parse(f.calls[2].init.body), { startupMode: 'Delayed' });
    f.calls[2].reject(new Error('startup failed'));
    await setImmediate();
    assert.equal(f.subject.startupMode.value, 'Disabled');
    assert.equal(f.subject.lanAccessEnabled.value, true);
    assert.equal(f.subject.errorMessage.value, '');
    assert.deepEqual(f.toasts.at(-1), ['startup failed', 'error']);
    assert.equal(f.calls[3].url, '/api/settings/general/lan-access');
    f.calls[3].resolve(response({}));
    await Promise.all([port, enabled, delayed, lan]);
    assert.equal(f.calls.length, 4);
    assert.equal(f.subject.savingKey.value, '');
  } finally { f.dispose(); }
});

test('old save replies cannot overwrite newer selections or an unsaved port draft', async () => {
  const f = await generalSettingsFixture();
  try {
    const first = f.subject.saveStartupMode('Enabled');
    const latest = f.subject.saveStartupMode('Delayed');
    f.calls[1].resolve(response({ startupMode: 'Enabled' }));
    await setImmediate();
    assert.equal(f.subject.startupMode.value, 'Delayed');
    f.calls[2].reject(new Error('latest failed'));
    await Promise.all([first, latest]);
    assert.equal(f.subject.startupMode.value, 'Enabled');
    f.subject.port.value = '12345';
    const saving = f.subject.savePort();
    f.subject.port.value = '23456';
    f.calls[3].resolve(response({ port: 12345 }));
    await saving;
    assert.equal(f.subject.port.value, '23456');
  } finally { f.dispose(); }
});

test('safe mode prevents queued general settings writes from starting', async () => {
  const f = await generalSettingsFixture();
  try {
    const active = f.subject.saveLanAccess(true);
    const queued = f.subject.savePort();
    f.safeModeEnabled.value = true;
    f.calls[1].resolve(response({}));
    await Promise.all([active, queued]);
    assert.equal(f.calls.length, 2);
    assert.equal(f.subject.savingKey.value, '');
  } finally { f.dispose(); }
});

test('general settings load only when visiting their tab and retain data across tab switches', async () => {
  for (const tab of ['notification', 'security', 'advanced', 'about']) {
    const f = fixture('SettingsView.vue', 'settingsLoaded,loadSettings', {}, { query: { tab } });
    try {
      f.mount();
      assert.equal(f.calls.length, 0);
      f.route.query.tab = 'general';
      await vue.nextTick();
      assert.equal(f.calls.length, 1);
      await f.subject.loadSettings();
      assert.equal(f.calls.length, 1);
      f.calls[0].resolve(response({ port: 9123, lanAccessEnabled: false, startupMode: 'Disabled' }));
      await setImmediate();
      assert.equal(f.subject.settingsLoaded.value, true);
      f.route.query.tab = tab;
      await vue.nextTick();
      f.route.query.tab = 'general';
      await vue.nextTick();
      assert.equal(f.calls.length, 1);
    } finally { f.dispose(); }
  }
});

test('failed general settings read can retry on returning and late results are ignored after leaving', async () => {
  const f = fixture('SettingsView.vue', 'settingsLoaded,port');
  try {
    f.mount();
    f.calls[0].reject(new Error('读取失败'));
    await setImmediate();
    f.route.query.tab = 'about';
    await vue.nextTick();
    f.route.query.tab = 'general';
    await vue.nextTick();
    assert.equal(f.calls.length, 2);
    f.dispose();
    f.calls[1].resolve(response({ port: 9999 }));
    await setImmediate();
    assert.equal(f.subject.settingsLoaded.value, false);
    assert.equal(f.subject.port.value, '9123');
  } finally { f.dispose(); }
});

test('general settings become loaded only after a successful read and retain saved values on refresh failure', async () => {
  const f = fixture('SettingsView.vue', 'loadSettings,settingsLoaded,port,lanAccessEnabled,startupMode');
  try {
    const initial = f.subject.loadSettings();
    assert.equal(f.subject.settingsLoaded.value, false);
    f.calls[0].resolve(response({ port: 12345, lanAccessEnabled: true, startupMode: 'Delayed' }));
    await initial;
    assert.equal(f.subject.settingsLoaded.value, true);
    assert.equal(f.subject.port.value, '12345');
    const refresh = f.subject.loadSettings();
    assert.equal(f.subject.settingsLoaded.value, true);
    f.calls[1].reject(new Error('Read failed'));
    await refresh;
    assert.equal(f.subject.settingsLoaded.value, true);
    assert.equal(f.subject.port.value, '12345');
    assert.equal(f.subject.lanAccessEnabled.value, true);
    assert.equal(f.subject.startupMode.value, 'Delayed');
  } finally { f.dispose(); }
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
  assert.equal(f.subject.updateNotesOpenModal.show, false);
  assert.equal(f.subject.updateNotesHtml.value, '<h2>改进</h2>');
  f.subject.updateNotesOpenModal.afterLeave();
  assert.equal(f.subject.updateNotesHtml.value, '');
  assert.equal(f.subject.updateNotes.value, '## 改进');
  const third = f.subject.openUpdateNotes();
  f.dispose();
  f.renders[2].resolve('<h2>晚到结果</h2>');
  await third;
  assert.equal(f.subject.updateNotesOpenModal.show, false);
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
    assert.equal(f.subject.updateNotesOpenModal.show, false);
    f.subject.updateNotesOpenModal.afterLeave();
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
