<script setup lang="ts">
import AppTooltip from '../../components/AppTooltip.vue';
import AppLinkButton from '../../components/AppLinkButton.vue';
import { NCard, NSwitch, NText } from 'naive-ui';
import type { PluginInfo } from './pluginModels';

const props = defineProps<{ plugin: PluginInfo; changing?: boolean }>();
const emit = defineEmits<{ toggle: [] }>();

function toggle() {
  emit('toggle');
}
</script>

<template>
  <NCard tag="section" content-class="plugin-home-header">
    <div class="plugin-home-header-main">
      <div class="plugin-home-header-copy">
        <NText tag="h2" :depth="1">{{ plugin.manifest?.name ?? plugin.directoryName }}</NText>
        <NText tag="p" :depth="2">{{ plugin.manifest?.description || '未提供插件描述' }}</NText>
      </div>
      <div class="plugin-card-actions">
        <AppTooltip :text="!plugin.canChangeEnabled ? '无法启用' : undefined" :disabled-trigger="!plugin.canChangeEnabled || changing">
          <NSwitch
            :aria-label="plugin.enabled ? '禁用插件' : '启用插件'"
            :value="plugin.enabled"
            :disabled="!plugin.canChangeEnabled || changing"
            @update:value="toggle"
          />
        </AppTooltip>
        <AppLinkButton :to="`/plugins/${encodeURIComponent(plugin.id)}/info`">
          详细信息
        </AppLinkButton>
        <AppLinkButton to="/plugins">返回</AppLinkButton>
      </div>
    </div>
  </NCard>
</template>

<style>
.plugin-card-actions { display: flex; align-self: center; align-items: center; gap: 10px; }
@media (max-width: 640px) {
  .plugin-card-actions { justify-content: space-between; }
}
.plugin-home-header-copy { min-width: 0; }
.plugin-home-header-copy p { max-width: 62ch; margin: 7px 0 0; font-size: 14px; line-height: 1.5; }
.plugin-home-header h2 { margin: 0; }
@media (max-width: 640px) {
  .plugin-home-header, .plugin-home-header-main { align-items: stretch; flex-direction: column; }
}
.plugin-home-header { display: grid; grid-template-columns: minmax(0, 1fr); align-items: stretch; justify-content: space-between; gap: 18px; }
.plugin-home-header-main { display: flex; width: 100%; min-width: 0; flex: 1; align-items: flex-start; justify-content: space-between; gap: 20px; }
.plugin-home-header-copy h2 { font-size: 18px; letter-spacing: -0.02em; }
.plugin-home-header .plugin-card-actions { align-items: center; flex-wrap: wrap; justify-content: flex-end; }
@media (max-width: 640px) {
  .plugin-home-header-main { gap: 16px; }
  .plugin-home-header .plugin-card-actions { width: 100%; justify-content: flex-end; }
}
@media (max-width: 767px) {
  .plugin-home-header .plugin-card-actions { align-self: stretch; justify-content: flex-start; }
}
</style>
