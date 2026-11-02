import { apiFetch } from './client';
import { combineRequestSignal } from './requestSignal';

// 合并页面与单次请求的取消信号，在响应解析前后检查，避免页面关闭后返回过期结果。
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
      // 处理器未消费响应体时主动取消读取，随后解除本次请求登记的取消监听。
      if (response?.body && !response.bodyUsed) await response.body.cancel();
    } catch {
    } finally {
      request.dispose();
      check();
    }
  }
}
