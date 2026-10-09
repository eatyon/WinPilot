import { ref } from 'vue'
import { accentPalettes, getAccentTokens, type AccentColor, type AccentTokens, type ResolvedTheme } from './themeColors'

export type ThemeMode = 'system' | 'light' | 'dark'

const themeStorageKey = 'winpilot.theme'
const accentStorageKey = 'winpilot.accent'
const themeMode = ref<ThemeMode>('system')
const accentColor = ref<AccentColor>('blue')
const resolvedTheme = ref<ResolvedTheme>('light')
const accentProperties: Record<keyof AccentTokens, string> = {
  text: '--accent-text',
  textHover: '--accent-text-hover',
  solid: '--accent-solid',
  solidHover: '--accent-solid-hover',
  contrast: '--accent-contrast',
  bg: '--accent-bg',
  border: '--accent-border',
  focusRing: '--accent-focus-ring'
}
let systemMedia: MediaQueryList | undefined

function applyAccent() {
  resolvedTheme.value = themeMode.value === 'dark' || (themeMode.value === 'system' && Boolean(systemMedia?.matches)) ? 'dark' : 'light'
  const tokens = getAccentTokens(accentColor.value, resolvedTheme.value)
  for (const [token, property] of Object.entries(accentProperties)) {
    document.documentElement.style.setProperty(property, tokens[token as keyof AccentTokens])
  }
}

function applyTheme(mode: ThemeMode) {
  themeMode.value = mode
  const root = document.documentElement
  if (mode === 'system') root.removeAttribute('data-theme')
  else root.dataset.theme = mode
  root.style.colorScheme = mode === 'system' ? 'light dark' : mode
  localStorage.setItem(themeStorageKey, mode)
  applyAccent()
}

function applyAccentColor(color: AccentColor) {
  accentColor.value = color
  localStorage.setItem(accentStorageKey, color)
  applyAccent()
}

export function initializeTheme() {
  if (!systemMedia) {
    systemMedia = window.matchMedia('(prefers-color-scheme: dark)')
    systemMedia.addEventListener('change', applyAccent)
  }
  const savedTheme = localStorage.getItem(themeStorageKey)
  const savedAccent = localStorage.getItem(accentStorageKey)
  accentColor.value = savedAccent && Object.hasOwn(accentPalettes, savedAccent) ? savedAccent as AccentColor : 'blue'
  applyTheme(savedTheme === 'light' || savedTheme === 'dark' ? savedTheme : 'system')
}

export function useTheme() {
  return { themeMode, accentColor, resolvedTheme, setTheme: applyTheme, setAccent: applyAccentColor }
}
