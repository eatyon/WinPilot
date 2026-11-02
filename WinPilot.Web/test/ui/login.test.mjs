import { readStylesheet, readFeatureStyles } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import * as vue from 'vue';
import { loadScriptModule } from '../support/scriptModule.mjs';
import { readApiError, userErrorMessage } from '../support/apiErrors.mjs';

test('login uses a desktop card and centered mobile form with program control styling', () => {
  const source = readFileSync(new URL('../../src/features/auth/LoginView.vue', import.meta.url), 'utf8');
  const css = readFeatureStyles();
  assert.match(source, /<h1 id="login-title">管理员登录<\/h1>/);
  assert.match(source, /autocomplete: 'current-password'/);
  assert.match(source, /<AppPasswordInput/);
  assert.doesNotMatch(source, /本机控制台|login-security-note|尽在掌握/);
  const panel = css.match(/\.login-panel\s*\{([^}]+)\}/)?.[1];
  assert.match(panel, /width: min\(100%, 360px\)/);
  assert.doesNotMatch(panel, /box-shadow|background:|border:/);
  assert.match(source, /<NCard[\s\S]*?class="login-panel"[\s\S]*?content-class="login-panel-content"/);
  assert.match(css, /@media \(min-width: 641px\)\s*\{\s*\.login-panel\s*\{\s*width: min\(100%, 418px\);/);
  assert.match(css, /\.login-panel-content\s*\{\s*display: grid; gap: 30px;/);
  assert.match(css, /@media \(max-width: 640px\)\s*\{\s*\.login-view\s*\{\s*position: relative;\s*padding: 108px 28px;/);
  assert.match(css, /\.login-brand\s*\{\s*position: absolute; top: 32px;/);
  assert.match(css, /\.login-view \.login-panel\.n-card\s*\{\s*position: static;[^}]*border: 0;/);
  assert.match(css, /\.login-view \.login-panel > \.login-panel-content:first-child\s*\{ padding: 0; \}/);
  assert.doesNotMatch(css, /\.login-view\s*\{[^}]*align-items: start/);
  assert.match(css, /\.login-form \.login-submit\s*\{\s*width: 100%;\s*\}/);
  assert.equal((css.match(/\.login-form \.login-submit\s*\{/g) ?? []).length, 1);
  assert.match(css, /\.login-footer\s*\{[^}]*border-top: 1px solid var\(--border\);[^}]*text-align: center;/);
  assert.match(css, /\.login-footer\s*\{\s*position: absolute;[^}]*bottom: max\(24px, env\(safe-area-inset-bottom\)\);/);
});

test('login uses the library scrollbar without reserving a native scrollbar gutter', () => {
  const source = readFileSync(new URL('../../src/features/auth/LoginView.vue', import.meta.url), 'utf8');
  const css = readFeatureStyles();
  const base = readStylesheet(new URL('../../src/styles/base.css', import.meta.url));
  assert.match(base, /html\s*\{[^}]*scrollbar-gutter: stable;/);
  assert.match(source, /<NScrollbar class="login-scrollbar">\s*<main class="login-view">/);
  assert.match(css, /html:has\(\.login-view\)\s*\{\s*overflow: hidden; scrollbar-gutter: auto;/);
  assert.match(css, /body:has\(\.login-view\)\s*\{\s*overflow: hidden; scrollbar-gutter: auto;/);
  assert.match(css, /\.login-scrollbar\s*\{\s*height: 100dvh;/);
  assert.match(css, /\.login-view\s*\{[^}]*min-height: 100dvh;/);
  assert.match(base, /body\s*\{[^}]*background: var\(--canvas\);/);
});

const code = loadScriptModule(new URL('../../src/features/auth/LoginView.vue', import.meta.url),
  'password, error, isSubmitting, submit, lockSeconds, loginError');

function fixture() {
  const requests = [];
  const navigations = [];
  const timers = new Map();
  let now = 0;
  let unmount;
  const context = {
    exports: {}, AbortController, Error,
    Date: class extends Date { static now() { return now; } },
    setInterval: callback => { timers.set(callback, callback); return callback; },
    clearInterval: timer => timers.delete(timer),
    require: name => name === 'vue' ? { ...vue, useId: () => 'login-test', onBeforeUnmount: callback => { unmount = callback; } }
      : name === 'vue-router' ? { useRouter: () => ({ replace: path => navigations.push(path) }) }
      : name.endsWith('/ui/theme') ? { useTheme: () => ({ themeMode: vue.ref('system'), setTheme() {} }) }
      : name.endsWith('/client') ? {
        readApiError, userErrorMessage,
        apiFetch: (url, options) => new Promise(resolve => requests.push({ url, options, resolve })),
      } : {},
  };
  const scope = vue.effectScope();
  scope.run(() => runInNewContext(code, context));
  return {
    subject: context.exports, requests, navigations, timers,
    advance: milliseconds => { now += milliseconds; for (const tick of timers.values()) tick(); },
    dispose: () => { unmount(); scope.stop(); },
  };
}

test('login retries preserve the existing error until the response and password edits clear it', async () => {
  const f = fixture();
  try {
    const { subject: login, requests } = f;
    login.password.value = 'wrong-password';
    const first = login.submit();
    requests[0].resolve(new Response(JSON.stringify({ message: '密码错误' }), { status: 401 }));
    await first;
    assert.equal(login.error.value, '密码错误');

    const retry = login.submit();
    assert.equal(login.error.value, '密码错误');
    assert.equal(login.isSubmitting.value, true);
    await login.submit();
    assert.equal(requests.length, 2);
    requests[1].resolve(new Response(null, { status: 401 }));
    await retry;
    assert.equal(login.error.value, '密码不正确或请求已失效');
    assert.equal(login.isSubmitting.value, false);

    login.password.value = 'changed-password';
    assert.equal(login.error.value, '');
    const success = login.submit();
    requests[2].resolve(new Response(null, { status: 204 }));
    await success;
    assert.deepEqual(f.navigations, ['/home']);
  } finally { f.dispose(); }
});

test('lock countdown uses the server duration and elapsed time, blocks requests and stops on expiry or unmount', async () => {
  const f = fixture();
  try {
    const login = f.subject;
    const locked = login.submit();
    f.requests[0].resolve(new Response(null, { status: 429, headers: { 'Retry-After': '272' } }));
    await locked;
    assert.equal(login.loginError.value, '登录已锁定，请 4 分 32 秒后重试');
    login.password.value = 'changed-password';
    await login.submit();
    assert.equal(f.requests.length, 1);
    f.advance(12500);
    assert.equal(login.lockSeconds.value, 260);
    assert.equal(login.loginError.value, '登录已锁定，请 4 分 20 秒后重试');
    f.advance(259500);
    assert.equal(login.lockSeconds.value, 0);
    assert.equal(login.loginError.value, '');
    assert.equal(f.timers.size, 0);
    const stillLocked = login.submit();
    f.requests[1].resolve(new Response(null, { status: 429, headers: { 'Retry-After': '30' } }));
    await stillLocked;
    assert.equal(login.loginError.value, '登录已锁定，请 30 秒后重试');
    assert.equal(f.timers.size, 1);
  } finally { f.dispose(); }
  assert.equal(f.timers.size, 0);
});

test('missing or invalid retry durations do not invent a five-minute countdown', async () => {
  for (const value of ['', 'invalid', '0', '-1']) {
    const f = fixture();
    try {
      const pending = f.subject.submit();
      f.requests[0].resolve(new Response(null, { status: 429, headers: value ? { 'Retry-After': value } : {} }));
      await pending;
      assert.equal(f.subject.loginError.value, '登录已锁定，请稍后重试');
      assert.equal(f.subject.lockSeconds.value, 0);
      assert.equal(f.timers.size, 0);
    } finally { f.dispose(); }
  }
});

test('a late lock response after leaving the login page cannot create a countdown timer', async () => {
  const f = fixture();
  const pending = f.subject.submit();
  f.dispose();
  assert.equal(f.requests[0].options.signal.aborted, true);
  f.requests[0].resolve(new Response(null, { status: 429, headers: { 'Retry-After': '300' } }));
  await pending;
  assert.equal(f.timers.size, 0);
  assert.equal(f.subject.lockSeconds.value, 0);
});
