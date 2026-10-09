<script setup lang="ts">
import { RouterLink } from 'vue-router';
import type { PluginInfo } from './PluginModels';

defineProps<{ plugin: PluginInfo; changing?: boolean }>();
const emit = defineEmits<{ toggle: [] }>();
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
          :class="{ 'is-invalid-enabled': plugin.state === 'Invalid' && plugin.enabled }"
          :title="
            plugin.state === 'Invalid'
              ? plugin.canChangeEnabled
                ? plugin.enabled
                  ? '关闭插件（当前版本不兼容）'
                  : '启用插件（当前版本不兼容，不会加载）'
                : '无效插件无法修改'
              : plugin.enabled
                ? '关闭插件'
                : '开启插件'
          "
        >
          <input
            type="checkbox"
            :aria-label="plugin.enabled ? '关闭插件' : '开启插件'"
            :checked="plugin.enabled"
            :disabled="!plugin.canChangeEnabled || changing"
            @change="emit('toggle')"
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
