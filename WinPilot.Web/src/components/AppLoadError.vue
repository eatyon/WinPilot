<script setup lang="ts">
import { NAlert, NButton, NCollapseTransition, NFlex, NText } from 'naive-ui';
import { ref } from 'vue';
import AppIcon from './AppIcon.vue';
import AppTooltip from './AppTooltip.vue';
import { useDelayedLoading } from '../ui/useDelayedLoading';

const props = withDefaults(defineProps<{
  show?: boolean;
  retry?: () => unknown;
  disabled?: boolean;
  loading?: boolean;
}>(), { show: true });
const retrying = ref(false);
const showLoading = useDelayedLoading(() => Boolean(props.loading || retrying.value));

async function retryLoad() {
  if (!props.retry || props.disabled || props.loading || retrying.value) return;
  retrying.value = true;
  try {
    await props.retry();
  } finally {
    retrying.value = false;
  }
}
</script>

<template>
  <NCollapseTransition :show="show" :inert="!show || undefined" class="status-message error app-load-error">
    <div class="load-error-content">
      <NAlert type="error" :show-icon="false" role="alert">
        <NFlex align="center" :wrap="false" :size="12">
          <NText type="error" class="load-error-icon">
            <AppIcon name="alert-circle" :size="20" />
          </NText>
          <span class="load-error-message"><slot /></span>
          <AppTooltip v-if="retry" text="重试">
            <NButton
              attr-type="button"
              size="small"
              text
              aria-label="重试"
              class="load-error-retry"
              :loading="showLoading"
              :disabled="disabled || loading || retrying"
              :aria-busy="loading || retrying"
              @click="retryLoad"
            >
              <template #icon><AppIcon name="rotate-cw" :size="18" /></template>
            </NButton>
          </AppTooltip>
        </NFlex>
      </NAlert>
    </div>
  </NCollapseTransition>
</template>

<style scoped>
.app-load-error { grid-column: 1 / -1; }
.app-load-error:not(:last-child) { margin-bottom: calc(-1 * var(--load-error-layout-gap, 0px)); }
.load-error-content { padding-bottom: 18px; }
.load-error-icon { display: flex; flex-shrink: 0; }
.load-error-message { flex: 1; min-width: 0; overflow-wrap: anywhere; }
.load-error-retry { flex-shrink: 0; }
</style>
