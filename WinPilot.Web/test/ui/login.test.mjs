import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import * as vue from 'vue';
import { loadScriptModule } from '../support/scriptModule.mjs';
import { readApiError, userErrorMessage } from '../support/apiErrors.mjs';

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
    require: name => name === 'vue' ? { ...vue, onBeforeUnmount: callback => { unmount = callback; } }
      : name === 'vue-router' ? { useRouter: () => ({ replace: path => navigations.push(path) }) }
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
