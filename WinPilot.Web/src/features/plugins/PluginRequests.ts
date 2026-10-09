import { apiFetch } from '../../api/client';
import { combinePageSignal } from './page/PluginPageLifetime';

export function createPluginRequests() {
  const controllers = new Map<string, { controller: AbortController; method: string; dispose: () => void }>();
  const signals = new WeakMap<Response, AbortSignal>();
  let disposed = false;
  const check = (signal?: AbortSignal) => {
    if (disposed || signal?.aborted) throw new DOMException('页面请求已取消', 'AbortError');
  };
  const cancelAll = () => {
    for (const request of controllers.values()) { request.controller.abort(); request.dispose(); }
    controllers.clear();
  };
  return {
    async fetch(url: string, options: RequestInit = {}) {
      check();
      const method = options.method ?? 'GET';
      const key = `${method}:${url}`;
      const previous = controllers.get(key);
      previous?.controller.abort();
      previous?.dispose();
      const controller = new AbortController();
      const request = combinePageSignal(controller.signal, options.signal ?? undefined);
      controllers.set(key, { controller, method, dispose: request.dispose });
      try {
        const response = await apiFetch(url, { ...options, signal: request.signal });
        check(request.signal);
        signals.set(response, request.signal);
        return response;
      } catch (error) { request.dispose(); check(request.signal); throw error; }
    },
    async json<T = unknown>(response: Response): Promise<T> {
      let result: unknown;
      try { result = await response.json(); }
      catch (error) { check(signals.get(response)); throw error; }
      check(signals.get(response));
      return result as T;
    },
    async text(response: Response) {
      let result: string;
      try { result = await response.text(); }
      catch (error) { check(signals.get(response)); throw error; }
      check(signals.get(response));
      return result;
    },
    cancelReads() {
      for (const [key, request] of controllers)
        if (request.method === 'GET' || request.method === 'HEAD') {
          request.controller.abort();
          request.dispose();
          controllers.delete(key);
        }
    },
    dispose() {
      disposed = true;
      cancelAll();
    },
    cancelAll,
    get active() { return !disposed; },
  };
}
