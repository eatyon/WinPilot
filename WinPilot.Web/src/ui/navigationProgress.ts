import { readonly, ref } from 'vue';

const status = ref<'idle' | 'loading' | 'error'>('idle');
let activeNavigation: object | undefined;
export const navigationStatus = readonly(status);

export function startNavigation(target: object) {
  activeNavigation = target;
  status.value = 'loading';
}

export function finishNavigation(target: object, failed = false) {
  if (target !== activeNavigation) return;
  activeNavigation = undefined;
  status.value = failed ? 'error' : 'idle';
}
