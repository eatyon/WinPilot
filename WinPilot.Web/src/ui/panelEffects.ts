import { readonly, ref } from 'vue';

export const pageTransitionEffectOptions = [
  { value: 'fade', label: '淡入淡出' },
  { value: 'fade-slide', label: '滑动淡入' },
  { value: 'scale', label: '缩放' },
  { value: 'slide', label: '滑动' },
] as const;
type PageTransitionEffect = typeof pageTransitionEffectOptions[number]['value'];
function isPageTransitionEffect(value: unknown): value is PageTransitionEffect {
  return pageTransitionEffectOptions.some(option => option.value === value);
}
const dialogBlurEnabled = ref(false);
const pageTransitionEnabled = ref(true);
const interactionAnimationEnabled = ref(true);
const loadingBarEnabled = ref(true);
const pageTransitionEffect = ref<PageTransitionEffect>('fade');
const storageKey = 'winpilot.panel-effects';
let reducedMotionMedia: MediaQueryList | undefined;

function applyEffects() {
  document.documentElement.classList.toggle('dialog-blur-enabled', dialogBlurEnabled.value);
  document.documentElement.classList.toggle(
    'interaction-animations-disabled',
    !interactionAnimationEnabled.value || Boolean(reducedMotionMedia?.matches),
  );
}

function saveEffects() {
  applyEffects();
  try {
    localStorage.setItem(storageKey, JSON.stringify({
      dialogBlurEnabled: dialogBlurEnabled.value,
      pageTransitionEnabled: pageTransitionEnabled.value,
      interactionAnimationEnabled: interactionAnimationEnabled.value,
      loadingBarEnabled: loadingBarEnabled.value,
      pageTransitionEffect: pageTransitionEffect.value,
    }));
  } catch {}
}

export function initializePanelEffects() {
  if (!reducedMotionMedia) {
    reducedMotionMedia = window.matchMedia('(prefers-reduced-motion: reduce)');
    reducedMotionMedia.addEventListener('change', applyEffects);
  }
  try {
    const saved = JSON.parse(localStorage.getItem(storageKey) ?? 'null');
    dialogBlurEnabled.value = saved?.dialogBlurEnabled === true;
    pageTransitionEnabled.value = saved?.pageTransitionEnabled !== false;
    interactionAnimationEnabled.value = saved?.interactionAnimationEnabled !== false;
    loadingBarEnabled.value = saved?.loadingBarEnabled !== false;
    pageTransitionEffect.value = isPageTransitionEffect(saved?.pageTransitionEffect)
      ? saved.pageTransitionEffect
      : 'fade';
  } catch {
    dialogBlurEnabled.value = false;
    pageTransitionEnabled.value = true;
    interactionAnimationEnabled.value = true;
    loadingBarEnabled.value = true;
    pageTransitionEffect.value = 'fade';
  }
  applyEffects();
}

export function usePanelEffects() {
  return {
    dialogBlurEnabled: readonly(dialogBlurEnabled),
    pageTransitionEnabled: readonly(pageTransitionEnabled),
    interactionAnimationEnabled: readonly(interactionAnimationEnabled),
    loadingBarEnabled: readonly(loadingBarEnabled),
    pageTransitionEffect: readonly(pageTransitionEffect),
    setDialogBlurEnabled(value: boolean) { dialogBlurEnabled.value = value; saveEffects(); },
    setPageTransitionEnabled(value: boolean) { pageTransitionEnabled.value = value; saveEffects(); },
    setInteractionAnimationEnabled(value: boolean) { interactionAnimationEnabled.value = value; saveEffects(); },
    setLoadingBarEnabled(value: boolean) { loadingBarEnabled.value = value; saveEffects(); },
    setPageTransitionEffect(value: PageTransitionEffect) {
      if (!isPageTransitionEffect(value)) return;
      pageTransitionEffect.value = value;
      saveEffects();
    },
  };
}
