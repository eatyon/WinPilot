<script setup lang="ts">
import AppModal from '../../../components/AppModal.vue';
import { ref } from 'vue';
import type { PluginModalEntry } from './PluginPageModalStack';
import PluginPageRenderer from './PluginPageRenderer.vue';

const props = defineProps<{ modal: PluginModalEntry; active: boolean; depth: number }>();
const emit = defineEmits<{ dismiss: []; accept: []; action: [index: number]; failed: [error: unknown] }>();
const loading = ref(props.modal.kind === 'content');
</script>

<template>
  <AppModal
    :title="modal.options.title || '提示'"
    :size="modal.kind === 'content' ? modal.options.size : 'small'"
    :dismiss-on-backdrop="modal.kind === 'content' && modal.options.dismissOnBackdrop === true"
    :dismissible="modal.kind === 'confirmation' || (modal.options.dismissible !== false && !modal.pending)"
    :inert="!active ? true : undefined"
    :aria-hidden="!active ? true : undefined"
    :style="{ zIndex: 50 + depth }"
    @close="emit('dismiss')"
  >
    <p v-if="modal.kind === 'confirmation'">{{ modal.options.message }}</p>
    <PluginPageRenderer v-else :view="modal.options" :signal="modal.controller.signal" @ready="loading = false" @failed="cause => emit('failed', cause)" />
    <p v-if="loading" class="plugin-empty loading-hint">正在加载…</p>
    <template v-if="modal.kind === 'confirmation' || !modal.options.actions || modal.options.actions.length" #actions>
      <template v-if="modal.kind === 'confirmation'">
        <button class="button button-secondary" type="button" autofocus @click="emit('dismiss')">取消</button>
        <button class="button" :class="modal.options.danger ? 'button-danger' : 'button-primary'" type="button" @click="emit('accept')">
          {{ modal.options.acceptText || '确认' }}
        </button>
      </template>
      <template v-else-if="modal.options.actions">
        <button
          v-for="(action, index) in modal.options.actions"
          :key="index"
          class="button"
          :class="`button-${action.kind || 'secondary'}`"
          :disabled="loading || action.disabled || modal.pending"
          type="button"
          @click="emit('action', index)"
        >{{ action.label }}</button>
      </template>
      <button v-else class="button button-secondary" :disabled="modal.options.dismissible === false || modal.pending" type="button" @click="emit('dismiss')">取消</button>
    </template>
  </AppModal>
</template>
