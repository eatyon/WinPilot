<script setup lang="ts">
import AppIcon from '../../components/AppIcon.vue';
import AppModal from '../../components/AppModal.vue';
import { showToast } from '../../ui/toast';
import type { RegisteredVariable } from './taskModels';

defineProps<{
  variables: readonly RegisteredVariable[];
  loading: boolean;
  error: string;
}>();
const emit = defineEmits<{ close: [] }>();

function copyTextLegacy(text: string, container: HTMLElement): boolean {
  const input = document.createElement('textarea');
  const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  input.value = text;
  input.readOnly = true;
  input.style.position = 'fixed';
  input.style.left = '-9999px';
  input.style.opacity = '0';
  input.setAttribute('aria-hidden', 'true');
  if (!container.isConnected) return false;
  container.append(input);
  try {
    input.select();
    return document.execCommand('copy');
  } catch {
    return false;
  } finally {
    input.remove();
    previousFocus?.focus({ preventScroll: true });
  }
}
async function copyVariable(name: string, event: Event) {
  const container = event.currentTarget instanceof HTMLElement
    ? event.currentTarget.closest<HTMLElement>('[role="dialog"]')
    : null;
  if (!container) return;
  try {
    if (window.isSecureContext && navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(name);
      showToast('变量已复制', 'success');
      return;
    }
  } catch {
  }
  if (copyTextLegacy(name, container)) showToast('变量已复制', 'success');
  else showToast('自动复制失败，请选中文本手动复制');
}
</script>

<template>
  <AppModal title="可用变量" :dismiss-on-backdrop="false" @close="emit('close')">
    <div class="variable-help-content">
      <p class="field-hint">变量由 WinPilot 内置或已启用的插件注册提供</p>
      <p v-if="loading" class="loading-hint">正在读取变量…</p>
      <p v-else-if="error" class="status-message error">{{ error }}</p>
      <p v-else-if="!variables.length">暂无可用变量</p>
      <div v-else class="variable-help-list">
        <article v-for="variable in variables" :key="variable.name" class="variable-help-item">
          <div class="variable-help-code">
            <code>{{ variable.name }}</code>
            <button
              class="variable-copy-button"
              type="button"
              :title="`复制 ${variable.name}`"
              :aria-label="`复制 ${variable.name}`"
              @click="copyVariable(variable.name, $event)"
            >
              <AppIcon name="copy" />
            </button>
          </div>
          <strong>{{ variable.displayName }}</strong>
          <small>{{ variable.description || '未提供变量说明' }}</small>
          <small>来源：{{ variable.pluginDisplayName }}</small>
        </article>
      </div>
    </div>
  </AppModal>
</template>
