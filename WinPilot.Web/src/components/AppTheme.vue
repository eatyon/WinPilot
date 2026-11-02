<script setup lang="ts">
import { computed, onScopeDispose, ref } from 'vue';
import { darkTheme, lightTheme, NConfigProvider, NLoadingBarProvider, NMessageProvider, zhCN, dateZhCN } from 'naive-ui';
import AppToast from './AppToast.vue';
import AppRouteLoading from './AppRouteLoading.vue';
import AppThemeTokens from './AppThemeTokens.vue';
import { useTheme } from '../ui/theme';
import { getAccentTokens } from '../ui/themeColors';
import { createComponentTheme } from '../ui/componentTheme';
import { usePanelEffects } from '../ui/panelEffects';

const { resolvedTheme, accentColor } = useTheme();
const { loadingBarEnabled } = usePanelEffects();
const mobileMedia = window.matchMedia('(max-width: 599px)');
const mobile = ref(mobileMedia.matches);
function updateMobile() { mobile.value = mobileMedia.matches; }
mobileMedia.addEventListener('change', updateMobile);
onScopeDispose(() => mobileMedia.removeEventListener('change', updateMobile));
const overrides = computed(() => createComponentTheme(getAccentTokens(accentColor.value, resolvedTheme.value), resolvedTheme.value, mobile.value));
</script>

<template>
  <NConfigProvider abstract :locale="zhCN" :date-locale="dateZhCN" :theme="resolvedTheme === 'dark' ? darkTheme : lightTheme" :theme-overrides="overrides">
    <AppThemeTokens />
    <NLoadingBarProvider :container-style="loadingBarEnabled ? undefined : { display: 'none' }">
      <AppRouteLoading />
      <NMessageProvider placement="top" :duration="4200" :container-style="{ top: '76px' }">
        <AppToast />
        <slot />
      </NMessageProvider>
    </NLoadingBarProvider>
  </NConfigProvider>
</template>
