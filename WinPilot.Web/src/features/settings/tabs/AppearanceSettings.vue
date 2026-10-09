<script setup lang="ts">
import { useTheme, type ThemeMode } from '../../../ui/theme';
import { accentOptions, getAccentTokens } from '../../../ui/themeColors';
const { themeMode, accentColor, resolvedTheme, setTheme, setAccent } = useTheme();
const options: Array<{ value: ThemeMode; label: string; description: string }> = [
  { value: 'system', label: '跟随系统', description: '跟随当前系统设置' },
  { value: 'light', label: '浅色', description: '始终使用浅色模式' },
  { value: 'dark', label: '深色', description: '始终使用深色模式' },
];
</script>
<template>
  <div class="settings-tab-content settings-appearance-content">
    <section class="settings-section settings-appearance-section">
      <div class="settings-section-heading">
        <h3>模式</h3>
        <p>选择界面的显示模式，修改后立即生效</p>
      </div>
      <div class="theme-options" role="radiogroup" aria-label="模式">
        <label
          v-for="option in options"
          :key="option.value"
          class="theme-option"
          :class="{ active: themeMode === option.value }"
        >
          <input
            type="radio"
            name="theme"
            :value="option.value"
            :checked="themeMode === option.value"
            @change="setTheme(option.value)"
          />
          <span class="theme-option-copy">
            <strong>{{ option.label }}</strong>
            <small>{{ option.description }}</small>
          </span>
          <span class="theme-option-indicator" aria-hidden="true"></span>
        </label>
      </div>
    </section>
    <section class="settings-section settings-appearance-section">
      <div class="settings-section-heading">
        <h3>主题色</h3>
        <p>选择界面的主题色，修改后立即生效</p>
      </div>
      <div class="accent-options" role="radiogroup" aria-label="主题色">
        <label
          v-for="option in accentOptions"
          :key="option.value"
          class="accent-option"
          :class="{ active: accentColor === option.value }"
        >
          <input
            type="radio"
            name="accent"
            :value="option.value"
            :checked="accentColor === option.value"
            @change="setAccent(option.value)"
          />
          <span
            class="accent-swatch"
            :style="{ backgroundColor: getAccentTokens(option.value, resolvedTheme).solid }"
          ></span>
          <strong>{{ option.label }}</strong>
          <span class="accent-check" aria-hidden="true">✓</span>
        </label>
      </div>
    </section>
  </div>
</template>
