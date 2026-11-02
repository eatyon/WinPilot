import { ref } from 'vue';
import { accentOptions, type AccentColor, type ResolvedTheme } from './themeColors';

export type ThemeMode = 'system' | 'light' | 'dark';

const themeStorageKey = 'winpilot.theme';
const accentStorageKey = 'winpilot.accent';
const themeMode = ref<ThemeMode>('system');
const accentColor = ref<AccentColor>('blue');
const resolvedTheme = ref<ResolvedTheme>('light');
let systemMedia: MediaQueryList | undefined;

function updateResolvedTheme() {
  resolvedTheme.value = themeMode.value === 'dark' || (themeMode.value === 'system' && Boolean(systemMedia?.matches)) ? 'dark' : 'light';
}

function applyTheme(mode: ThemeMode) {
  themeMode.value = mode;
  const root = document.documentElement;
  if (mode === 'system') root.removeAttribute('data-theme');
  else root.dataset.theme = mode;
  localStorage.setItem(themeStorageKey, mode);
  updateResolvedTheme();
}

function applyAccentColor(color: AccentColor) {
  accentColor.value = color;
  localStorage.setItem(accentStorageKey, color);
  updateResolvedTheme();
}

export function initializeTheme() {
  if (!systemMedia) {
    systemMedia = window.matchMedia('(prefers-color-scheme: dark)');
    systemMedia.addEventListener('change', updateResolvedTheme);
  }
  const savedTheme = localStorage.getItem(themeStorageKey);
  const savedAccent = localStorage.getItem(accentStorageKey);
  accentColor.value = accentOptions.some(option => option.value === savedAccent) ? savedAccent as AccentColor : 'blue';
  applyTheme(savedTheme === 'light' || savedTheme === 'dark' ? savedTheme : 'system');
}

export function useTheme() {
  return { themeMode, accentColor, resolvedTheme, setTheme: applyTheme, setAccent: applyAccentColor };
}
