import { executeRequest } from '../../api/requestExecutor';

export function createPluginRequests() {
  const controllers = new Map<string, { controller: AbortController; method: string }>();
  let disposed = false;
  const cancelAll = () => {
    for (const [key, request] of [...controllers]) {
      request.controller.abort();
      if (controllers.get(key) === request) controllers.delete(key);
    }
  };
  return {
    async run<T>(url: string, options: RequestInit, handleResponse: (response: Response) => T | Promise<T>): Promise<T> {
      if (disposed) throw new DOMException('页面请求已取消', 'AbortError');
      const method = options.method ?? 'GET';
      const key = `${method}:${url}`;
      const previous = controllers.get(key);
      previous?.controller.abort();
      const controller = new AbortController();
      const request = { controller, method };
      controllers.set(key, request);
      try {
        return await executeRequest(url, options, controller, handleResponse);
      } finally {
        if (controllers.get(key) === request) controllers.delete(key);
      }
    },
    cancelReads() {
      for (const [key, request] of [...controllers])
        if (request.method === 'GET' || request.method === 'HEAD') {
          request.controller.abort();
          if (controllers.get(key) === request) controllers.delete(key);
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
