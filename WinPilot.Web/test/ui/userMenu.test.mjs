import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import * as vue from 'vue';
import { loadScriptModule } from '../support/scriptModule.mjs';

function fixture() {
  const calls = [], toasts = [], redirects = [];
  const safeModeEnabled = vue.ref(false);
  let dispose, mount;
  const context = { exports: {}, AbortController, require(name) {
    if (name === 'vue') return { ...vue, onMounted: callback => { mount = callback; }, onBeforeUnmount: callback => { dispose = callback; } };
    if (name.endsWith('/client')) return {
      apiFetch: (url, init) => new Promise((resolve, reject) => calls.push({ url, init, resolve, reject })),
      readApiError: async () => '请求失败',
      isAbortError: error => error.name === 'AbortError',
      userErrorMessage: (error, fallback) => error.message || fallback,
      redirectToLogin: () => redirects.push('/login'),
    };
    if (name.endsWith('/toast')) return { showToast: message => toasts.push(message) };
    if (name.endsWith('/useDelayedLoading')) return { useDelayedLoading: () => vue.ref(false) };
    if (name.endsWith('/safeMode')) return { safeModeEnabled };
    if (name === 'naive-ui') return { NText: vue.defineComponent({}), useThemeVars: () => vue.ref({ errorColor: '#d03050' }) };
    return {};
  } };
  runInNewContext(loadScriptModule(new URL('../../src/features/auth/UserMenu.vue', import.meta.url),
    'options, shown, loadingPasswordState, loggingOut, menuTheme, loadPasswordState, updateShow, select'), context);
  return { subject: context.exports, calls, toasts, redirects, safeModeEnabled, mount: () => mount(), dispose: () => dispose() };
}

test('safe mode adds a non-interactive notice while retaining the logout action', async () => {
  const f = fixture();
  try {
    const pending = f.subject.loadPasswordState();
    f.calls[0].resolve(new Response(JSON.stringify({ configured: true })));
    await pending;
    f.safeModeEnabled.value = true;
    const options = f.subject.options.value;
    assert.equal(options.length, 3);
    assert.equal(options[0].type, 'render');
    assert.equal(options[0].render().children.default(), '安全模式已开启，部分功能不可用');
    assert.equal(options[1].type, 'divider');
    assert.equal(options[2].label, '退出登录');
    assert.equal(options[2].disabled, false);
    f.safeModeEnabled.value = false;
    assert.equal(f.subject.options.value.length, 1);
  } finally { f.dispose(); }
});

test('user menu reads password state on mount and reuses it when reopening', async () => {
  for (const configured of [true, false]) {
    const f = fixture();
    try {
      assert.equal(f.calls.length, 0);
      const pending = f.mount();
      assert.equal(f.subject.options.value[0].disabled, true);
      assert.equal(f.subject.menuTheme.value, undefined);
      assert.equal(f.subject.options.value[0].label, '退出登录');
      f.subject.updateShow(true);
      f.subject.updateShow(false);
      f.subject.updateShow(true);
      await f.subject.select('logout');
      assert.equal(f.calls.length, 1);
      f.calls[0].resolve(new Response(JSON.stringify({ configured })));
      await pending;
      assert.equal(f.subject.options.value[0].label, '退出登录');
      assert.equal(f.subject.options.value[0].disabled, !configured);
      assert.equal(f.subject.menuTheme.value?.optionTextColor, configured ? '#d03050' : undefined);
      f.subject.updateShow(true);
      f.subject.updateShow(false);
      f.subject.updateShow(true);
      assert.equal(f.calls.length, 1);
      if (!configured) {
        await f.subject.select('logout');
        assert.equal(f.calls.length, 1);
      }
    } finally { f.dispose(); }
  }
});

test('a password response arriving after unmount cannot enable logout or report an error', async () => {
  for (const failed of [false, true]) {
    const f = fixture();
    try {
      const pending = f.mount();
      f.dispose();
      assert.equal(f.calls[0].init.signal.aborted, true);
      if (failed) f.calls[0].reject(new Error('late failure'));
      else f.calls[0].resolve(new Response(JSON.stringify({ configured: true })));
      await pending;
      assert.equal(f.subject.options.value[0].disabled, true);
      assert.deepEqual(f.toasts, []);
      await f.subject.select('logout');
      assert.equal(f.calls.length, 1);
    } finally { f.dispose(); }
  }
});

test('logout prevents duplicate submissions and redirects after success or an expired session', async () => {
  for (const status of [204, 401]) {
    const f = fixture();
    try {
      const load = f.mount();
      f.calls[0].resolve(new Response(JSON.stringify({ configured: true })));
      await load;
      const pending = f.subject.select('logout');
      await f.subject.select('logout');
      assert.equal(f.calls.length, 2);
      assert.equal(f.calls[1].url, '/api/logout');
      assert.equal(f.calls[1].init.method, 'POST');
      assert.equal(f.subject.options.value[0].disabled, true);
      assert.equal(f.redirects.length, 0);
      f.calls[1].resolve(new Response(null, { status }));
      await pending;
      assert.deepEqual(f.redirects, ['/login']);
    } finally { f.dispose(); }
  }
});

test('failed requests unlock the menu for retry and unmounted results cannot navigate', async () => {
  const f = fixture();
  try {
    const load = f.mount();
    f.calls[0].reject(new Error('offline'));
    await load;
    assert.equal(f.subject.options.value[0].disabled, true);
    assert.equal(f.subject.loadingPasswordState.value, false);
    f.subject.updateShow(true);
    assert.equal(f.calls.length, 2);
    f.calls[1].resolve(new Response(JSON.stringify({ configured: true })));
    await new Promise(resolve => setImmediate(resolve));
    const failedLogout = f.subject.select('logout');
    f.calls[2].resolve(new Response(null, { status: 500 }));
    await failedLogout;
    assert.equal(f.subject.loggingOut.value, false);
    assert.deepEqual(f.toasts, ['offline', '请求失败']);
    assert.equal(f.redirects.length, 0);
    const pending = f.subject.select('logout');
    f.dispose();
    assert.equal(f.calls[3].init.signal.aborted, true);
    f.calls[3].resolve(new Response(null, { status: 204 }));
    await pending;
    assert.equal(f.redirects.length, 0);
  } finally { f.dispose(); }
});
