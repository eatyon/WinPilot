import { nextTick, onScopeDispose, ref, watch } from 'vue';
import { showToast } from './toast';

// 展开前按需准备选项，用版本号丢弃关闭或重新打开之前尚未完成的准备结果。
export function useSelectOpening(disabled: () => boolean, beforeOpen: () => (() => Promise<boolean | void>) | undefined) {
  const open = ref(false);
  const loading = ref(false);
  let version = 0;
  let active = true;
  function close() {
    version++;
    open.value = false;
    loading.value = false;
  }
  async function updateShow(show: boolean) {
    if (!show) { close(); return; }
    if (!active || disabled() || loading.value) return;
    const request = ++version;
    const prepare = beforeOpen();
    if (!prepare) {
      open.value = true;
      return;
    }
    loading.value = true;
    try {
      const allowed = await prepare();
      await nextTick();
      if (active && request === version && !disabled() && allowed !== false) open.value = true;
    } catch {
      if (active && request === version) showToast('选项加载失败，请稍后重试');
    } finally {
      if (active && request === version) loading.value = false;
    }
  }
  watch(disabled, value => { if (value && !loading.value) close(); }, { flush: 'sync' });
  onScopeDispose(() => { active = false; close(); });
  return { open, loading, updateShow };
}
