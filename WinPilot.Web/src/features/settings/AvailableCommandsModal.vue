<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import { createSettingsRequests } from './SettingsRequests';
import AppModal from '../../components/AppModal.vue';
import { isAbortError, userErrorMessage } from '../../api/client';

defineProps<{ prefix: string }>();
const requests = createSettingsRequests();
const emit = defineEmits<{ close: [] }>();
const groups = ref<Array<{ name: string; commands: Array<{ usage: string; description: string }> }>>([]);
const loading = ref(true);
const error = ref('');

async function load() {
  loading.value = true;
  error.value = '';
  try {
    const response = await requests.fetch('/api/settings/command-bots/commands');
    if (!response.ok) throw new Error('无法读取可用命令');
    groups.value = await requests.json(response);
  } catch (cause) {
    if (requests.active && !isAbortError(cause)) error.value = userErrorMessage(cause, '无法读取可用命令');
  } finally {
    loading.value = false;
  }
}

onMounted(load);
onBeforeUnmount(() => requests.dispose());
</script>

<template>
  <AppModal title="可用命令" :dismiss-on-backdrop="false" @close="emit('close')">
    <div class="available-commands">
      <p v-if="loading" class="field-hint" role="status">加载中…</p>
      <div v-else-if="error">
        <p class="status-message error" role="alert">{{ error }}</p>
        <button type="button" class="button button-secondary" @click="load">重试</button>
      </div>
      <section v-for="group in groups" v-else :key="group.name" class="command-group">
        <h3>{{ group.name }}</h3>
        <dl>
          <div v-for="command in group.commands" :key="command.usage" class="command-item">
            <dt>{{ prefix }}{{ command.usage }}</dt>
            <dd>{{ command.description }}</dd>
          </div>
        </dl>
      </section>
    </div>
  </AppModal>
</template>

<style scoped>
.available-commands { max-height: 65dvh; overflow-y: auto; overflow-wrap: anywhere; padding-right: 8px; }
.command-group { border: 1px solid var(--border-strong); border-radius: var(--radius-md); }
.command-group + .command-group { margin-top: 20px; }
.command-group h3 { margin: 0; padding: 12px; border-bottom: 1px solid var(--border); font-size: 16px; font-weight: 700; }
.command-group dl { display: grid; gap: 8px; margin: 0; padding: 12px; }
.command-item { padding: 10px 12px; border-radius: var(--radius-sm); background: var(--surface-subtle); }
.command-item dt { font-weight: 600; white-space: pre-wrap; user-select: text; }
.command-item dd { margin: 4px 0 0; color: var(--text-muted); font-size: 13px; line-height: 1.6; }
</style>
