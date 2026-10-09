<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue';
import { useRouter } from 'vue-router';
import AppIcon from '../../components/AppIcon.vue';
import { apiFetch, userErrorMessage } from '../../api/client';

const router = useRouter();
const password = ref('');
const passwordVisible = ref(false);
const error = ref('');
const isSubmitting = ref(false);
let active = true;
let controller: AbortController | undefined;
onBeforeUnmount(() => { active = false; controller?.abort(); });
async function submit() {
  if (!active || isSubmitting.value) return;
  controller = new AbortController();
  const request = controller;
  error.value = '';
  isSubmitting.value = true;
  try {
    const response = await apiFetch('/login', {
      signal: request.signal,
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password: password.value }),
    });
    if (!response.ok) {
      const problem = (await response.json().catch(() => undefined)) as
        { errors?: Record<string, string[]> } | undefined;
      throw new Error(
        problem?.errors?.password?.[0] ??
          (response.status === 429 ? '登录尝试过多，请 5 分钟后重试' : '密码不正确或请求已失效'),
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
</script>

<template>
  <main class="login-view">
    <section class="login-panel" aria-labelledby="login-title">
      <div class="login-brand">
        <span class="login-brand-symbol">W</span>
        <span>WinPilot</span>
      </div>

      <div class="login-copy">
        <p class="section-kicker">本机控制台</p>
        <h1 id="login-title">登录 WinPilot</h1>
        <p>输入管理员密码以继续访问此设备</p>
      </div>

      <form class="login-form" @submit.prevent="submit">
        <label class="login-field">
          <span>管理员密码</span>
          <span class="login-password-input">
            <input
              v-model="password"
              :type="passwordVisible ? 'text' : 'password'"
              name="password"
              autocomplete="current-password"
              placeholder="请输入密码"
            />
            <button
              type="button"
              class="login-password-toggle"
              :aria-label="passwordVisible ? '隐藏密码' : '显示密码'"
              :title="passwordVisible ? '隐藏密码' : '显示密码'"
              @click="passwordVisible = !passwordVisible"
            >
              <AppIcon :name="passwordVisible ? 'eye-off' : 'eye'" />
            </button>
          </span>
        </label>

        <p v-if="error" class="login-error" role="alert">{{ error }}</p>
        <button class="button button-primary login-submit" type="submit" :disabled="isSubmitting">
          {{ isSubmitting ? '请稍候' : '登录' }}
          <AppIcon name="arrow" />
        </button>
      </form>

      <p class="login-security-note">
        <AppIcon name="shield" />
        此页面仅用于本机管理员访问
      </p>
    </section>
  </main>
</template>
