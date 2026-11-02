<script setup lang="ts">
import AppTooltip from './AppTooltip.vue';
import { NCard, NCollapseTransition, NDrawer, NDrawerContent, NRadio, NRadioGroup, NSwitch } from 'naive-ui';
import { useTheme } from '../ui/theme';
import { accentOptions, type AccentColor } from '../ui/themeColors';
import { pageTransitionEffectOptions, usePanelEffects } from '../ui/panelEffects';
import AppSelect from './AppSelect.vue';
import AppIcon from './AppIcon.vue';

defineProps<{ show: boolean }>();
const emit = defineEmits<{ 'update:show': [value: boolean] }>();
const { themeMode, resolvedTheme, accentColor, setTheme, setAccent } = useTheme();
const {
  dialogBlurEnabled,
  pageTransitionEnabled,
  interactionAnimationEnabled,
  loadingBarEnabled,
  pageTransitionEffect,
  setDialogBlurEnabled,
  setPageTransitionEnabled,
  setInteractionAnimationEnabled,
  setLoadingBarEnabled,
  setPageTransitionEffect,
} = usePanelEffects();
function setAutomaticTheme(enabled: boolean) {
  setTheme(enabled ? 'system' : resolvedTheme.value);
}
function setDarkMode(enabled: boolean) {
  setTheme(enabled ? 'dark' : 'light');
}
</script>

<template>
  <NDrawer :show="show" placement="right" :width="320" @update:show="emit('update:show', $event)">
    <NDrawerContent title="外观设置" closable :native-scrollbar="false">
      <div class="appearance-settings">
        <NCard title="主题" size="small">
          <div class="appearance-settings-fields">
            <div class="appearance-settings-row">
              <span>自动切换主题</span>
              <NSwitch :value="themeMode === 'system'" aria-label="自动切换主题" @update:value="setAutomaticTheme">
                <template #checked>自动</template>
                <template #unchecked>手动</template>
              </NSwitch>
            </div>
            <NCollapseTransition :show="themeMode !== 'system'" :inert="themeMode === 'system' || undefined">
              <div class="appearance-settings-row appearance-settings-dependent">
                <span>主题模式</span>
                <NSwitch :value="resolvedTheme === 'dark'" aria-label="主题模式" @update:value="setDarkMode">
                  <template #checked>深色</template>
                  <template #unchecked>浅色</template>
                </NSwitch>
              </div>
            </NCollapseTransition>
          </div>
        </NCard>
        <NCard title="强调色" size="small">
          <NRadioGroup
            class="appearance-accent-group"
            :value="accentColor"
            name="appearance-accent"
            aria-label="强调色"
            @update:value="value => setAccent(value as AccentColor)"
          >
            <div class="appearance-settings-colors">
              <AppTooltip v-for="(option, index) in accentOptions" :key="option.value" :text="option.label" :placement="index < 5 ? 'top' : 'bottom'">
                <NRadio :value="option.value" class="appearance-accent-option">
                  <span class="appearance-accent-name">{{ option.label }}</span>
                  <span
                    class="appearance-accent-swatch"
                    :class="{ 'is-selected': accentColor === option.value }"
                    :style="{ backgroundColor: option.color }"
                    aria-hidden="true"
                  >
                    <AppIcon v-if="accentColor === option.value" name="check-circle" />
                  </span>
                </NRadio>
              </AppTooltip>
            </div>
          </NRadioGroup>
        </NCard>
        <NCard title="动画效果" size="small">
          <div class="appearance-settings-fields">
            <div class="appearance-settings-row">
              <span>交互动画</span>
              <NSwitch :value="interactionAnimationEnabled" aria-label="交互动画" @update:value="setInteractionAnimationEnabled" />
            </div>
            <div class="appearance-settings-row appearance-settings-dependent">
              <span>页面切换动画</span>
              <NSwitch :value="pageTransitionEnabled" aria-label="页面切换动画" @update:value="setPageTransitionEnabled" />
            </div>
            <NCollapseTransition :show="pageTransitionEnabled" :inert="!pageTransitionEnabled || undefined">
              <div class="appearance-settings-row appearance-settings-dependent">
                <span>页面切换效果</span>
                <AppSelect
                  class="appearance-page-transition-effect"
                  :model-value="pageTransitionEffect"
                  :options="pageTransitionEffectOptions"
                  aria-label="页面切换效果"
                  @update:model-value="$event && setPageTransitionEffect($event)"
                />
              </div>
            </NCollapseTransition>
          </div>
        </NCard>
        <NCard title="视觉效果" size="small">
          <div class="appearance-settings-fields">
            <div class="appearance-settings-row">
              <span>顶部进度条</span>
              <NSwitch :value="loadingBarEnabled" aria-label="顶部进度条" @update:value="setLoadingBarEnabled" />
            </div>
            <div class="appearance-settings-row appearance-settings-dependent">
              <span>对话框背景模糊</span>
              <NSwitch :value="dialogBlurEnabled" aria-label="对话框背景模糊" @update:value="setDialogBlurEnabled" />
            </div>
          </div>
        </NCard>
      </div>
    </NDrawerContent>
  </NDrawer>
</template>

<style scoped>
.appearance-settings { display: grid; gap: 16px; }
.appearance-settings-fields { display: grid; }
.appearance-settings-dependent { padding-top: 16px; }
.appearance-settings-row { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.appearance-page-transition-effect { width: 120px; flex: 0 0 auto; }
.appearance-accent-group { display: block; width: 100%; }
.appearance-settings-colors { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 12px 6px; }
.appearance-accent-option { position: relative; display: flex; align-items: center; justify-content: center; width: 100%; height: 44px; margin: 0; border-radius: 50%; }
.appearance-accent-option :deep(.n-radio__label) { padding: 0; line-height: 0; }
.appearance-accent-name, .appearance-accent-option :deep(.n-radio__dot-wrapper) { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
.appearance-accent-swatch { display: inline-flex; align-items: center; justify-content: center; width: 36px; height: 36px; border-radius: 50%; color: #fff; box-shadow: 0 3px 8px #0002; }
.appearance-accent-swatch.is-selected { outline: 3px solid var(--accent-border); outline-offset: 2px; }
.appearance-accent-option:has(input:focus-visible) { outline: 2px solid var(--text); outline-offset: 3px; }
.appearance-accent-swatch svg { width: 22px; height: 22px; }
</style>
