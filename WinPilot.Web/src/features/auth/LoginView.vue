<script setup lang="ts">
import AppPasswordInput from '../../components/AppPasswordInput.vue';
import { NButton, NCard, NDropdown, NFormItem, NScrollbar } from 'naive-ui';
import { computed, onBeforeUnmount, ref, useId, watch } from 'vue';
import { useRouter } from 'vue-router';
import AppIcon from '../../components/AppIcon.vue';
import { readApiError, apiFetch, userErrorMessage } from '../../api/client';
import { useTheme, type ThemeMode } from '../../ui/theme';

const { themeMode, setTheme } = useTheme();
const themeOptions = [
  {
    type: 'group',
    label: '主题',
    key: 'theme',
    children: [
      { label: '自动切换', key: 'system' },
      { label: '浅色模式', key: 'light' },
      { label: '深色模式', key: 'dark' },
    ],
  },
];
function selectTheme(key: string) {
  setTheme(key as ThemeMode);
}

const router = useRouter();
const password = ref('');
const error = ref('');
const isSubmitting = ref(false);
const lockSeconds = ref(0);
const loginError = computed(() => {
  if (lockSeconds.value === 0) return error.value;
  const minutes = Math.floor(lockSeconds.value / 60);
  const seconds = lockSeconds.value % 60;
  return `登录已锁定，请 ${minutes > 0 ? `${minutes} 分 ` : ''}${seconds} 秒后重试`;
});
let lockedUntil = 0;
let lockTimer: ReturnType<typeof setInterval> | undefined;
function stopLockTimer() {
  if (lockTimer !== undefined) clearInterval(lockTimer);
  lockTimer = undefined;
}
// 将服务端返回的锁定剩余时长换算为本地截止时间，避免计时器节流导致倒计时累计偏差。
function updateLockSeconds() {
  lockSeconds.value = Math.max(0, Math.ceil((lockedUntil - Date.now()) / 1000));
  if (lockSeconds.value === 0) stopLockTimer();
}
let active = true;
let controller: AbortController | undefined;
watch(
  password,
  () => {
    error.value = '';
  },
  { flush: 'sync' },
);
onBeforeUnmount(() => {
  active = false;
  controller?.abort();
  stopLockTimer();
});
async function submit() {
  if (!active || isSubmitting.value) return;
  updateLockSeconds();
  if (lockSeconds.value > 0) return;
  controller = new AbortController();
  const request = controller;
  isSubmitting.value = true;
  try {
    const response = await apiFetch('/login', {
      signal: request.signal,
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password: password.value }),
    });
    if (!active || request.signal.aborted) return;
    // 前端倒计时只用于展示和禁用按钮，实际登录锁定仍由后端按来源检查。
    if (response.status === 429) {
      const retryAfter = Number(response.headers.get('Retry-After'));
      if (Number.isFinite(retryAfter) && retryAfter > 0) {
        error.value = '';
        lockedUntil = Date.now() + retryAfter * 1000;
        stopLockTimer();
        updateLockSeconds();
        lockTimer = setInterval(updateLockSeconds, 1000);
        return;
      }
    }
    if (!response.ok) {
      throw new Error(
        await readApiError(
          response,
          response.status === 429 ? '登录已锁定，请稍后重试' : '密码不正确或请求已失效',
        ),
      );
    }
    if (active && !request.signal.aborted) await router.replace('/home');
  } catch (cause) {
    if (active && !request.signal.aborted) error.value = userErrorMessage(cause, '操作失败');
  } finally {
    if (controller === request) controller = undefined;
    isSubmitting.value = false;
  }
}
const passwordId = useId();
</script>

<template>
  <NScrollbar class="login-scrollbar">
    <main class="login-view">
      <NCard
        tag="section"
        class="login-panel"
        content-class="login-panel-content"
        :theme-overrides="{ color: 'color-mix(in srgb, var(--surface) 80%, transparent)' }"
        aria-labelledby="login-title"
      >
        <div class="login-brand">
          <span class="login-brand-symbol">W</span>
          <span>WinPilot</span>
          <div class="login-theme-switch">
            <NDropdown
              trigger="click"
              placement="bottom-end"
              :options="themeOptions"
              :value="themeMode"
              @select="selectTheme"
            >
              <NButton quaternary circle aria-label="主题" aria-haspopup="menu">
                <template #icon><AppIcon name="palette" /></template>
              </NButton>
            </NDropdown>
          </div>
        </div>

        <div class="login-copy">
          <h1 id="login-title">管理员登录</h1>
          <p>输入管理员密码以访问此设备</p>
        </div>

        <form class="login-form" @submit.prevent="submit">
          <NFormItem
            label="管理员密码"
            :label-props="{ for: passwordId }"
            :show-feedback="Boolean(loginError)"
            :validation-status="loginError ? 'error' : undefined"
            class="form-field login-field"
          >
            <AppPasswordInput
              v-model:value="password"
              placeholder="请输入密码"
              :input-props="{ id: passwordId, name: 'password', autocomplete: 'current-password' }"
            />
            <template #feedback><span role="alert">{{ loginError }}</span></template>
          </NFormItem>
          <NButton
            class="login-submit"
            :loading="isSubmitting"
            attr-type="submit"
            :disabled="isSubmitting || lockSeconds > 0"
            type="primary"
          >
            {{ isSubmitting ? '登录中' : '登录' }}
            <AppIcon name="arrow" />
          </NButton>
        </form>

        <p class="login-footer">WinPilot · 设备管理控制台</p>
      </NCard>
    </main>
  </NScrollbar>
</template>

<style>
html:has(.login-view) { overflow: hidden; scrollbar-gutter: auto; }
body:has(.login-view) { overflow: hidden; scrollbar-gutter: auto; background: transparent; }
.login-scrollbar { height: 100dvh; }
.login-view {
  display: grid;
  min-height: 100dvh;
  place-items: center;
  padding: 48px 24px;
  background:
    radial-gradient(circle at 100% 0%, transparent 219px, color-mix(in srgb, var(--brand-accent-text) 6%, transparent) 220px 260px, transparent 261px),
    radial-gradient(circle at 0% 100%, transparent 169px, color-mix(in srgb, var(--brand-accent-text) 5%, transparent) 170px 204px, transparent 205px),
    radial-gradient(ellipse at 8% 0%, color-mix(in srgb, var(--brand-accent) 20%, var(--brand-surface)), transparent 55%),
    radial-gradient(ellipse at 100% 100%, color-mix(in srgb, var(--brand-accent) 12%, var(--brand-canvas)), transparent 50%),
    var(--brand-surface);
}
.login-theme-switch { margin-left: auto; flex-shrink: 0; }
.login-panel { width: min(100%, 360px); min-width: 0; }
.login-panel-content { display: grid; gap: 30px; }
.login-brand { display: flex; justify-content: flex-start; align-items: center; gap: 11px; margin-bottom: 8px; color: var(--text); font-size: 24px; font-weight: 600; }
.login-brand-symbol { width: 42px; height: 42px; flex: 0 0 42px; }
.login-copy { display: grid; gap: 10px; text-align: left; }
.login-copy h1 { margin: 0; font-size: 29px; line-height: 1.3; font-weight: 600; letter-spacing: -.5px; }
.login-copy p { margin: 0; color: var(--text-muted); font-size: 14px; line-height: 1.7; }
.login-form { display: grid; gap: 18px; }
.login-form .login-submit { width: 100%; }
.login-footer { margin: 0; padding-top: 24px; border-top: 1px solid var(--border); color: var(--text-subtle); font-size: 12px; line-height: 1.8; text-align: center; }
@media (min-width: 641px) {
  .login-panel { width: min(100%, 418px); }
  .login-view .login-panel > .login-panel-content:first-child { padding: 36px; }
  .login-brand { margin-bottom: 4px; font-size: 24px; }
}
@media (max-width: 640px) {
  .login-view {
    position: relative;
    padding: 108px 28px;
    background:
      radial-gradient(circle at 110% 0%, transparent 139px, color-mix(in srgb, var(--brand-accent-text) 5%, transparent) 140px 168px, transparent 169px),
      radial-gradient(ellipse at 8% 0%, color-mix(in srgb, var(--brand-accent) 20%, var(--brand-surface)), transparent 55%),
      radial-gradient(ellipse at 100% 100%, color-mix(in srgb, var(--brand-accent) 12%, var(--brand-canvas)), transparent 50%),
      var(--brand-surface);
  }
  .login-view .login-panel.n-card { position: static; width: min(100%, 400px); border: 0; background: transparent; box-shadow: none; }
  .login-view .login-panel > .login-panel-content:first-child { padding: 0; }
  .login-brand { position: absolute; top: 32px; right: max(28px, calc((100% - 400px) / 2)); left: max(28px, calc((100% - 400px) / 2)); margin-bottom: 0; font-size: 24px; }
  .login-brand-symbol { width: 38px; height: 38px; flex-basis: 38px; }
  .login-footer { position: absolute; right: max(28px, calc((100% - 400px) / 2)); bottom: max(24px, env(safe-area-inset-bottom)); left: max(28px, calc((100% - 400px) / 2)); }
}
</style>
