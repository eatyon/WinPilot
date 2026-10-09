export class PluginPageSessionExpiredError extends Error {
  constructor() { super('页面会话已失效，请重新打开功能页'); }
}

export async function readPluginResponse(response: Response): Promise<unknown> {
  if (response.status === 404) throw new PluginPageSessionExpiredError();
  let result: unknown;
  try { result = await response.json(); }
  catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error;
    throw new Error(response.ok ? '插件返回的数据不是有效 JSON' : `插件通信失败（HTTP ${response.status}）`);
  }
  if (!response.ok) {
    const detail = result && typeof result === 'object' && 'detail' in result ? result.detail : undefined;
    throw new Error(typeof detail === 'string' && detail ? detail : `插件通信失败（HTTP ${response.status}）`);
  }
  return result;
}
