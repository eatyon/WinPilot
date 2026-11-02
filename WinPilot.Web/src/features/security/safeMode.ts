import { readonly, ref } from 'vue';
import { apiFetch, redirectToLogin } from '../../api/client';

const enabled = ref(false);
let loaded = false;
let loading: Promise<void> | undefined;
let loadController: AbortController | undefined;
let events: EventSource | undefined;
let syncCheckGeneration = 0;
let stateVersion = 0;

export const safeModeEnabled = readonly(enabled);

// 并发调用复用首次加载；若 SSE 已更新状态，旧查询结果不能覆盖新状态。
export async function loadSafeMode(): Promise<void> {
  if (loaded) return;
  if (loading) return loading;
  const version = stateVersion;
  const controller = new AbortController();
  loadController = controller;
  loading = (async () => {
    try {
      const response = await apiFetch('/api/security/safe-mode', { cache: 'no-store', signal: controller.signal });
      if (response.ok) {
        const state = await response.json() as { enabled?: boolean };
        if (controller.signal.aborted || version !== stateVersion || typeof state.enabled !== 'boolean') return;
        enabled.value = state.enabled;
        loaded = true;
      }
    } catch (error) {
      if (!controller.signal.aborted) throw error;
    } finally {
      if (loadController === controller) {
        loadController = undefined;
        loading = undefined;
      }
    }
  })();
  return loading;
}

export function startSafeModeSync() {
  void loadSafeMode().catch(() => {});
  if (events) return;
  const source = new EventSource('/api/security/safe-mode/events');
  events = source;
  source.onmessage = event => {
    if (events !== source) return;
    try {
      const state = JSON.parse(event.data) as { enabled?: boolean };
      if (typeof state.enabled !== 'boolean') return;
      enabled.value = state.enabled;
      stateVersion++;
      loaded = true;
      syncCheckGeneration++;
    } catch { return; }
  };
  source.onerror = () => { if (events === source) void handleSyncError(source); };
}

// SSE 断开也可能只是网络故障，另查接口确认 401 后才触发登录失效跳转。
async function handleSyncError(source: EventSource): Promise<void> {
  const generation = ++syncCheckGeneration;
  let response: Response;
  try {
    response = await fetch('/api/security/safe-mode', { cache: 'no-store', signal: AbortSignal.timeout(1500) });
  } catch {
    return;
  }
  if (events !== source || generation !== syncCheckGeneration) return;
  if (response.status === 401) {
    redirectToLogin();
  } else if (response.ok) {
    const state = await response.json().catch(() => undefined) as { enabled?: boolean } | undefined;
    if (events !== source || generation !== syncCheckGeneration) return;
    if (typeof state?.enabled === 'boolean') {
      enabled.value = state.enabled;
      stateVersion++;
      loaded = true;
    }
  }
}

export function stopSafeModeSync() {
  stateVersion++;
  syncCheckGeneration++;
  const controller = loadController;
  loadController = undefined;
  loading = undefined;
  controller?.abort();
  events?.close();
  events = undefined;
}
