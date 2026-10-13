<script setup lang="ts">
import { RouterLink } from 'vue-router';
import type { PluginInfo } from './PluginModels';

const props = defineProps<{ plugin: PluginInfo; changing?: boolean }>();
const emit = defineEmits<{ toggle: [] }>();

function toggle(event: Event) {
  (event.target as HTMLInputElement).checked = props.plugin.enabled;
  emit('toggle');
}
</script>

<template>
  <section class="plugin-home-header">
    <div class="plugin-home-header-main">
      <div class="plugin-home-header-copy">
        <h2>{{ plugin.manifest?.name ?? plugin.directoryName }}</h2>
        <p>{{ plugin.manifest?.description || '未提供插件描述' }}</p>
      </div>
      <div class="plugin-card-actions">
        <label
          class="task-row-toggle"
          :title="!plugin.canChangeEnabled ? '无法启用' : plugin.enabled ? '禁用插件' : '启用插件'"
        >
          <input
            type="checkbox"
            :aria-label="plugin.enabled ? '禁用插件' : '启用插件'"
            :checked="plugin.enabled"
            :disabled="!plugin.canChangeEnabled || changing"
            @change="toggle"
          />
        </label>
        <RouterLink class="button button-secondary" :to="`/plugins/${encodeURIComponent(plugin.id)}/info`">
          详细信息
        </RouterLink>
        <RouterLink class="button button-secondary" to="/plugins">返回</RouterLink>
      </div>
    </div>
  </section>
</template>
