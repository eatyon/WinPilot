<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { NModal, NSpace } from 'naive-ui';
import { getPageScroller, lockScroll } from '../ui/scrollLock';

const props = withDefaults(
  defineProps<{
    title: string;
    dismissOnBackdrop?: boolean;
    dismissible?: boolean;
    size?: 'small' | 'medium' | 'large';
    bodyMaxHeight?: string;
    show?: boolean;
    inert?: boolean;
  }>(),
  { dismissOnBackdrop: true, dismissible: true, show: undefined },
);
const emit = defineEmits<{ close: []; 'after-leave': [] }>();
// 未传 show 时内部控制离场；受控模式由父组件更新 show，两种模式均在离场后通知清理。
const localShow = ref(true);
const shown = computed(() => props.show ?? localShow.value);
function requestClose() {
  if (!props.dismissible) return;
  if (props.show === undefined) localShow.value = false;
  else emit('close');
}
function afterLeave() {
  if (props.show === undefined) emit('close');
  emit('after-leave');
}
let releaseScroll: (() => void) | undefined;
onMounted(() => {
  releaseScroll = lockScroll(getPageScroller());
});
onBeforeUnmount(() => releaseScroll?.());
</script>

<template>
  <NModal
    :show="shown"
    :internal-appear="true"
    :inert="!shown || inert || undefined"
    preset="card"
    class="app-modal"
    :class="size && `app-modal-${size}`"
    :style="{ '--dialog-body-max-height': bodyMaxHeight }"
    :closable="dismissible"
    :close-focusable="true"
    :mask-closable="dismissible && dismissOnBackdrop"
    :close-on-esc="dismissible"
    content-class="modal-content"
    :header-style="{ paddingBottom: 'calc(var(--n-padding-bottom) - 4px)' }"
    :content-style="{ paddingTop: '4px', paddingBottom: $slots.actions ? 'calc(var(--n-padding-bottom) - 4px)' : undefined }"
    :footer-style="{ paddingTop: '4px' }"
    content-scrollable
    @update:show="
      (show) => {
        if (!show) requestClose();
      }
    "
    @after-leave="afterLeave"
  >
    <template #header>
      <span class="modal-title-with-extra">{{ title }}<slot name="title-extra" /></span>
    </template>
    <template v-if="$slots['header-actions']" #header-extra>
      <slot name="header-actions" />
    </template>
    <slot />
    <template v-if="$slots.actions" #footer>
      <NSpace class="modal-actions" justify="end"><slot name="actions" :close="requestClose" /></NSpace>
    </template>
  </NModal>
</template>

<style>
.modal-content > p { margin: 0; color: var(--text-muted); line-height: 1.6; transition: color .3s cubic-bezier(.4, 0, .2, 1); }
.app-modal { display: flex; flex-direction: column; width: min(var(--dialog-width, 560px), calc(100vw - 32px)); max-height: calc(100svh - 48px); }
.app-modal-small { --dialog-width: 420px; }
.app-modal-large { --dialog-width: 960px; }
.app-modal > .n-card__content-scrollbar { max-height: calc(var(--dialog-body-max-height, 100svh) + var(--n-padding-bottom, 0px)); }
.app-modal .modal-content { overflow: visible; }
.modal-content { min-height: 0; }
.app-modal > .n-card__content-scrollbar > .n-scrollbar-container { overscroll-behavior: contain; }
.modal-title-with-extra { display: inline-flex; align-items: center; gap: 8px; }
</style>
