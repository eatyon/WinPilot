import { ref, watch } from 'vue';

// 延后展示加载反馈以避免短请求闪烁；真实 loading 仍由调用方用于防止重复操作。
export function useDelayedLoading(loading: () => boolean, delay = 150) {
  const visible = ref(false);
  watch(loading, (value, _, onCleanup) => {
    visible.value = false;
    if (!value) return;
    const timer = setTimeout(() => { visible.value = true; }, delay);
    onCleanup(() => clearTimeout(timer));
  }, { immediate: true, flush: 'sync' });
  return visible;
}
