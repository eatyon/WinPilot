import { apiFetch } from './client';
import { combineRequestSignal } from './requestSignal';

export async function executeRequest<T>(
  url: string,
  options: RequestInit,
  controller: AbortController,
  handleResponse: (response: Response) => T | Promise<T>,
): Promise<T> {
  const request = combineRequestSignal(controller.signal, options.signal ?? undefined);
  const check = () => {
    if (request.signal.aborted) throw new DOMException('页面请求已取消', 'AbortError');
  };
  let response: Response | undefined;
  try {
    check();
    response = await apiFetch(url, { ...options, signal: request.signal });
    check();
    const result = await handleResponse(response);
    check();
    return result;
  } catch (error) {
    check();
    throw error;
  } finally {
    try {
      if (response?.body && !response.bodyUsed) await response.body.cancel();
    } catch {
    } finally {
      request.dispose();
      check();
    }
  }
}
