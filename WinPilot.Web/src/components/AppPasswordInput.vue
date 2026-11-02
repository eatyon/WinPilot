<script setup lang="ts">
import { NButton, NInput } from 'naive-ui';
import AppIcon from './AppIcon.vue';

defineOptions({ inheritAttrs: false });
withDefaults(defineProps<{ disabled?: boolean }>(), { disabled: undefined });
</script>

<template>
  <NInput v-bind="$attrs" :disabled="disabled" type="password" show-password-on="click">
    <template #password-invisible-icon>
      <NButton
        quaternary
        circle
        size="tiny"
        attr-type="button"
        :disabled="disabled"
        aria-label="显示密码"
        @mousedown.prevent
      >
        <AppIcon name="eye" />
      </NButton>
    </template>
    <template #password-visible-icon>
      <NButton quaternary circle size="tiny" attr-type="button" :disabled="disabled" aria-label="隐藏密码" @mousedown.prevent>
        <AppIcon name="eye-off" />
      </NButton>
    </template>
  </NInput>
</template>
