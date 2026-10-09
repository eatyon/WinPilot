import { apiFetch } from '../../api/client';

export function createSettingsRequests() {
  const controller = new AbortController();
  const ensureActive = () => {
    if (controller.signal.aborted) throw new DOMException('设置页面请求已取消', 'AbortError');
  };
  return {
    async fetch(url: string, options: Omit<RequestInit, 'signal'> = {}) {
      ensureActive();
      try {
        const response = await apiFetch(url, { ...options, signal: controller.signal });
        ensureActive();
        return response;
      } catch (error) { ensureActive(); throw error; }
    },
    async json<T = unknown>(response: Response): Promise<T> {
      try {
        const result = await response.json();
        ensureActive();
        return result as T;
      } catch (error) { ensureActive(); throw error; }
    },
    ensureActive,
    get active() { return !controller.signal.aborted; },
    dispose() { controller.abort(); },
  };
}
