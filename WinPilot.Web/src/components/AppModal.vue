<script setup lang="ts">
import { onBeforeUnmount, onMounted, onUnmounted, ref } from 'vue';
import AppIcon from './AppIcon.vue';
import { getPageScroller, lockScroll } from '../ui/scrollLock';
import { manageModalFocus } from '../ui/modalFocus';
import { manageModalTouch } from '../ui/modalTouch';

const props = withDefaults(defineProps<{ title: string; dismissOnBackdrop?: boolean; dismissible?: boolean; size?: 'small' | 'medium' | 'large' }>(), {
  dismissOnBackdrop: true,
  dismissible: true,
});
const emit = defineEmits<{ close: [] }>();
let releaseScroll: (() => void) | undefined;
let releaseFocus: (() => void) | undefined;
let releaseTouch: (() => void) | undefined;
const dialog = ref<HTMLElement>();
onMounted(() => {
  releaseScroll = lockScroll(getPageScroller());
  if (dialog.value) {
    releaseFocus = manageModalFocus(dialog.value);
    releaseTouch = manageModalTouch(dialog.value);
  }
});
onBeforeUnmount(() => { releaseTouch?.(); releaseScroll?.(); });
onUnmounted(() => releaseFocus?.());
</script>

<template>
  <div class="modal-backdrop" role="presentation" @click.self="props.dismissible && props.dismissOnBackdrop && emit('close')">
    <section ref="dialog" class="app-modal" :class="props.size && `app-modal-${props.size}`" role="dialog" aria-modal="true" :aria-label="title" tabindex="-1">
      <header class="modal-header">
        <h2 :class="{ 'modal-title-with-extra': $slots['title-extra'] }">{{ title }}<slot name="title-extra" /></h2>
        <div v-if="$slots['header-actions']" class="modal-header-actions">
          <slot name="header-actions" />
          <button class="modal-close" type="button" aria-label="关闭" :disabled="!props.dismissible" @click="emit('close')"><AppIcon name="close" /></button>
        </div>
        <button v-else class="modal-close" type="button" aria-label="关闭" :disabled="!props.dismissible" @click="emit('close')"><AppIcon name="close" /></button>
      </header>
      <div class="modal-content"><slot /></div>
      <footer v-if="$slots.actions" class="modal-actions"><slot name="actions" /></footer>
    </section>
  </div>
</template>

<style scoped>
.app-modal:focus { outline: none; }
.app-modal-small { width: min(100%, 420px); }
.app-modal-medium { width: min(100%, 560px); }
.app-modal-large { width: min(100%, 960px); }
@media (max-width: 760px) {
  .app-modal-small, .app-modal-medium, .app-modal-large { width: 100%; }
}
.modal-title-with-extra { display: flex; align-items: center; gap: 8px; }
.modal-header-actions { display: flex; align-items: center; gap: 4px; }
.modal-close { padding: 0; }
.modal-close .app-icon { width: 18px; height: 18px; }
</style>
