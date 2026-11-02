<script setup lang="ts">
import AppTooltip from '../../components/AppTooltip.vue';
import { NButton, NCard, NEmpty } from 'naive-ui';
import AppIcon from '../../components/AppIcon.vue';
import AppModal from '../../components/AppModal.vue';
import { showToast } from '../../ui/toast';
import type { RegisteredVariable } from './taskModels';

defineProps<{
  variables: readonly RegisteredVariable[];
}>();
const emit = defineEmits<{ close: [] }>();

function copyTextLegacy(text: string, container: HTMLElement): boolean {
  const input = document.createElement('textarea');
  const previousFocus =
    document.activeElement instanceof HTMLElement ? document.activeElement : null;
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
  const container =
    event.currentTarget instanceof HTMLElement
      ? event.currentTarget.closest<HTMLElement>('[role="dialog"]')
      : null;
  if (!container) return;
  try {
    if (window.isSecureContext && navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(name);
      showToast('变量已复制', 'success');
      return;
    }
  } catch {}
  if (copyTextLegacy(name, container)) showToast('变量已复制', 'success');
  else showToast('自动复制失败，请选中文本手动复制');
}
</script>

<template>
  <AppModal title="可用变量" body-max-height="min(58svh, 480px)" @close="emit('close')">
    <div class="variable-help-content">
      <p class="field-hint">变量由 WinPilot 内置或已启用的插件注册提供</p>
      <NEmpty v-if="!variables.length" :show-icon="false" description="暂无可用变量" />
      <div v-else class="variable-help-list">
        <NCard
          tag="article"
          v-for="variable in variables"
          :key="variable.name"
          content-class="variable-help-item"
          embedded
          size="small"
        >
          <div class="variable-help-code">
            <code>{{ variable.name }}</code>
            <AppTooltip :text="`复制 ${variable.name}`">
              <NButton
                quaternary
                size="tiny"
                class="app-icon-button variable-copy-button"
                :aria-label="`复制 ${variable.name}`"
                @click="copyVariable(variable.name, $event)"
              >
                <AppIcon name="copy" />
              </NButton>
            </AppTooltip>
          </div>
          <strong>{{ variable.displayName }}</strong>
          <small>{{ variable.description || '未提供变量说明' }}</small>
          <small>来源：{{ variable.pluginDisplayName }}</small>
        </NCard>
      </div>
    </div>
  </AppModal>
</template>

<style>
.variable-help-content { padding-right: 4px; }
.variable-help-content > :first-child { margin: 0 0 8px; }
.variable-help-content > :last-child { margin-bottom: 0; }
.variable-help-list { display: grid; gap: 8px; }
.variable-help-item { display: grid; gap: 3px; }
.variable-help-code { position: relative; min-width: 0; }
.variable-help-item code { display: block; width: 100%; padding-right: 32px; color: var(--accent-text); overflow-wrap: anywhere; }
.variable-copy-button { position: absolute; top: 50%; right: 2px; transform: translateY(-50%); }
.variable-help-item strong { font-size: 13px; font-weight: 500; }
.variable-help-item small { color: var(--text-muted); transition: color .3s cubic-bezier(.4, 0, .2, 1); }
</style>
