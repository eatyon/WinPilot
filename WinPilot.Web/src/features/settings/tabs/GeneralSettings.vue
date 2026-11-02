<script setup lang="ts">
import AppSelect from '../../../components/AppSelect.vue';
import AppNumberInput from '../../../components/AppNumberInput.vue';
import AppIcon from '../../../components/AppIcon.vue';
import AppLoadError from '../../../components/AppLoadError.vue';
import { computed, useId } from 'vue';
import { NFormItem, NAlert, NButton, NCard, NCheckbox, NSkeleton } from 'naive-ui';

const formId = useId();

const props = defineProps<{
  port: string;
  lanAccessEnabled: boolean;
  startupMode: string;
  isLoading: boolean;
  isLoaded: boolean;
  savingKey: '' | 'port' | 'lanAccess' | 'startupMode';
  isPortValid: boolean;
  errorMessage: string;
}>();
const initialLoading = computed(() => props.isLoading && !props.isLoaded && !props.errorMessage);
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
  <div class="settings-tab-content load-error-layout">
    <AppLoadError :show="Boolean(errorMessage)">{{ errorMessage }}</AppLoadError>
    <NCard>
      <div class="settings-section-heading">
        <h3>端口</h3>
        <p>用于在浏览器中访问 WinPilot Web 页面</p>
      </div>
      <NFormItem
        label="Web 服务端口"
        :label-props="{ for: 'general-port' }"
        :show-feedback="isLoaded && !isPortValid && port.length > 0"
        :validation-status="!isPortValid && port.length > 0 ? 'error' : undefined"
        feedback="端口必须是 1 到 65535 之间的整数"
        class="form-field"
      >
        <div class="settings-port-control">
          <div class="settings-port-field">
            <NSkeleton v-if="initialLoading" size="medium" :sharp="false" />
            <AppNumberInput
              v-else
              :model-value="port"
              id="general-port"
              :min="1"
              :max="65535"
              :step="1"
              inputmode="numeric"
              :aria-invalid="port.length > 0 && !isPortValid"
              :disabled="!isLoaded || isLoading || savingKey === 'port'"
              @update:model-value="emit('update:port', String($event))"
            />
          </div>
          <NButton
            class="settings-port-save"
            type="primary"
            :loading="savingKey === 'port'"
            :disabled="!isLoaded || isLoading || savingKey === 'port' || !isPortValid"
            @click="emit('save-port')"
          >
          {{ savingKey === 'port' ? '正在保存' : '保存' }}
          </NButton>
        </div>
      </NFormItem>
      <p class="settings-help">
        默认端口为
        <code>9123</code>
        。端口修改后需重启 WinPilot 生效
      </p>
    </NCard>
    <NCard>
      <div class="settings-section-heading">
        <h3>局域网访问</h3>
        <p>默认只允许当前电脑访问</p>
      </div>
      <NSkeleton v-if="initialLoading" :width="220" height="1lh" :sharp="false" />
      <NCheckbox
        v-else
        :checked="lanAccessEnabled"
        :disabled="!isLoaded || isLoading || savingKey === 'lanAccess'"
        @update:checked="emit('update:lanAccessEnabled', $event)"
      >
        允许局域网设备访问
      </NCheckbox>
      <NAlert type="warning" class="settings-security-notice" :show-icon="false">
        <div class="settings-security-notice-content">
          <AppIcon name="alert-circle" :size="22" color="var(--warning-text)" />
          <span>启用后将监听所有网卡，强烈建议先在“安全”中设置管理员密码</span>
        </div>
      </NAlert>
    </NCard>
    <NCard>
      <div class="settings-section-heading">
        <h3>开机启动</h3>
        <p>通过 Windows 计划任务在当前用户登录时启动 WinPilot</p>
      </div>
      <NFormItem
        label="启动模式"
        :label-props="{ id: (`${formId}-field-0`) + '-label' }"
        :show-feedback="false"
        class="form-field settings-startup-field"
      >
        <NSkeleton v-if="initialLoading" size="medium" :sharp="false" />
        <AppSelect
          :labelled-by="(`${formId}-field-0`) + '-label'"
          :id="`${formId}-field-0`"
          v-else
          aria-label="启动模式"
          :model-value="startupMode"
          :options="startupOptions.map(([value, label]) => ({ value, label }))"
          :disabled="!isLoaded || isLoading || savingKey === 'startupMode'"
          @change="selectStartupMode"
        />
      </NFormItem>
    </NCard>
  </div>
</template>

<style>
.settings-port-field { width: min(100%, 280px); }
.settings-port-field input { width: 100%; }
.settings-security-notice { margin-top: 9px; }
.settings-security-notice-content { display: flex; align-items: center; gap: 12px; }
.settings-security-notice-content > .app-icon { flex-shrink: 0; }
@media (max-width: 767px) {
  .settings-port-field { width: 100%; }
}
.settings-port-control { display: flex; width: 100%; align-items: center; gap: 10px; }
.settings-port-save { flex: 0 0 auto; }
@media (max-width: 640px) {
  .settings-port-field { flex: 1; min-width: 0; }
}
</style>
