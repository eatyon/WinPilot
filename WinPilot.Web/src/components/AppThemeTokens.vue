<script setup lang="ts">
import { onBeforeUnmount, watchEffect } from 'vue';
import { useThemeVars } from 'naive-ui';

const theme = useThemeVars();
const properties = {
  '--canvas': 'bodyColor',
  '--surface': 'cardColor',
  '--surface-hover': 'hoverColor',
  '--text': 'textColor1',
  '--text-muted': 'textColor2',
  '--text-subtle': 'textColor3',
  '--border': 'dividerColor',
  '--border-strong': 'borderColor',
  '--shadow-float': 'boxShadow2',
  '--accent-text': 'primaryColor',
  '--accent-text-hover': 'primaryColorHover',
  '--accent-solid': 'primaryColorSuppl',
  '--accent-solid-hover': 'primaryColorSuppl',
  '--accent-contrast': 'baseColor',
  '--accent-bg': 'hoverColor',
  '--accent-border': 'primaryColor',
  '--accent-focus-ring': 'primaryColor',
  '--success-text': 'successColor',
  '--success-bg': 'hoverColor',
  '--success-border': 'successColor',
  '--warning-text': 'warningColor',
  '--warning-bg': 'hoverColor',
  '--warning-border': 'warningColor',
  '--danger-text': 'errorColor',
  '--danger-bg': 'hoverColor',
  '--danger-border': 'errorColor',
  '--danger-solid': 'errorColor',
  '--danger-solid-hover': 'errorColorHover',
  '--danger-contrast': 'baseColor',
  '--control-height': 'heightMedium',
  '--control-font-size': 'fontSizeMedium',
  '--control-radius': 'borderRadius',
  '--scrollbar-color': 'scrollbarColor',
  '--scrollbar-color-hover': 'scrollbarColorHover',
  '--scrollbar-width': 'scrollbarWidth',
  '--scrollbar-height': 'scrollbarHeight',
  '--scrollbar-radius': 'scrollbarBorderRadius',
} as const;

watchEffect(() => {
  for (const [property, token] of Object.entries(properties)) {
    document.documentElement.style.setProperty(property, theme.value[token]);
  }
  document.documentElement.style.setProperty(
    '--surface-subtle',
    `oklch(from ${theme.value.cardColor} calc(l - 0.02) c h)`,
  );
});
onBeforeUnmount(() => {
  for (const property of Object.keys(properties)) document.documentElement.style.removeProperty(property);
  document.documentElement.style.removeProperty('--surface-subtle');
});
</script>

<template></template>
