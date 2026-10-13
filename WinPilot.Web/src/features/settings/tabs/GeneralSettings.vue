<script setup lang="ts">
import AppSelect from '../../../components/AppSelect.vue';

const props = defineProps<{
  port: string;
  lanAccessEnabled: boolean;
  startupMode: string;
  isLoading: boolean;
  isLoaded: boolean;
  isSaving: boolean;
  isPortValid: boolean;
  errorMessage: string;
}>();
const emit = defineEmits<{
  'update:port': [value: string];
  'save-port': [];
  'update:lanAccessEnabled': [value: boolean];
  'update:startupMode': [value: string];
}>();
const startupOptions = [
  ['Disabled', '关闭'],
  ['Enabled', '开启'],
  ['Delayed', '延迟开启'],
] as const;
function selectStartupMode(mode: string) {
  if (mode !== props.startupMode) emit('update:startupMode', mode);
}
</script>
<template>
  <div class="settings-tab-content">
    <p v-if="errorMessage" class="status-message error" role="alert">{{ errorMessage }}</p>
    <section class="settings-section">
      <div class="settings-section-heading">
        <h3>端口</h3>
        <p>用于在浏览器中访问 WinPilot Web 页面</p>
      </div>
      <div class="settings-port-control" :class="{ 'settings-data-pending': !isLoaded }">
        <label class="field-control settings-port-field">
          <span>Web 服务端口</span>
          <input
            :value="port"
            type="number"
            min="1"
            max="65535"
            step="1"
            inputmode="numeric"
            :aria-invalid="port.length > 0 && !isPortValid"
            :disabled="isLoading || isSaving"
            @input="emit('update:port', ($event.target as HTMLInputElement).value)"
          />
        </label>
        <button
          class="button button-primary settings-port-save"
          type="button"
          :disabled="isLoading || isSaving || !isPortValid"
          @click="emit('save-port')"
        >
          保存
        </button>
      </div>
      <p v-if="isLoaded && !isPortValid && port.length > 0" class="field-error">端口必须是 1 到 65535 之间的整数</p>
      <p class="settings-help">
        默认端口为
        <code>9123</code>
        。端口修改后需重启 WinPilot 生效
      </p>
    </section>
    <section class="settings-section">
      <div class="settings-section-heading">
        <h3>局域网访问</h3>
        <p>默认只允许当前电脑访问</p>
      </div>
      <label class="toggle-control settings-toggle" :class="{ 'settings-data-pending': !isLoaded }">
        <input
          :checked="lanAccessEnabled"
          type="checkbox"
          :disabled="isLoading || isSaving"
          @change="emit('update:lanAccessEnabled', ($event.target as HTMLInputElement).checked)"
        />
        <span>允许局域网设备访问</span>
      </label>
      <p class="settings-security-notice">
        开启前强烈建议在“安全”中设置管理员密码。启用后，将监听所有网卡
      </p>
    </section>
    <section class="settings-section">
      <div class="settings-section-heading">
        <h3>开机启动</h3>
        <p>通过 Windows 计划任务在当前用户登录时启动 WinPilot</p>
      </div>
      <div class="field-control settings-startup-field" :class="{ 'settings-data-pending': !isLoaded }">
        <span>启动模式</span>
        <AppSelect
          :model-value="startupMode"
          :options="startupOptions.map(([value, label]) => ({ value, label }))"
          :disabled="isLoading || isSaving"
          @change="selectStartupMode"
        />
      </div>
    </section>
  </div>
</template>
<style scoped>
.settings-startup-field {
  gap: 7px;
}
</style>
