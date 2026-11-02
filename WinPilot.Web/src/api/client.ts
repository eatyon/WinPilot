const connectionErrorMessage = '无法连接到 WinPilot';
let redirectingToLogin = false;

export function redirectToLogin(): void {
  if (redirectingToLogin || window.location.pathname === '/login') return;
  redirectingToLogin = true;
  window.location.assign('/login');
}

export class WinPilotConnectionError extends Error {
  constructor() {
    super(connectionErrorMessage);
  }
}

// 认证失效统一跳转，其余 HTTP 错误由业务读取；主动取消保持 AbortError。
export async function apiFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  let response: Response;
  try {
    response = await fetch(input, init);
  } catch (error) {
    if (isAbortError(error)) throw error;
    throw new WinPilotConnectionError();
  }
  if (response.status === 401) {
    const address = typeof input === 'object' && 'url' in input ? input.url : String(input);
    const url = new URL(address, window.location.href);
    const path = url.pathname.toLowerCase();
    if (url.origin === window.location.origin
      && (path === '/api' || path.startsWith('/api/') || path.startsWith('/plugin-assets/'))) {
      redirectToLogin();
    }
  }
  return response;
}

export function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError';
}

export function userErrorMessage(error: unknown, fallback: string): string {
  return error instanceof Error && error.message.trim() ? error.message : fallback;
}

// 兼容后端现有的 ProblemDetails、字段校验和 message 响应，优先显示具体原因。
export function apiErrorMessage(body: unknown, fallback: string): string {
  if (!body || typeof body !== 'object' || Array.isArray(body)) return fallback;
  const problem = body as Record<string, unknown>;
  const errors = problem.errors && typeof problem.errors === 'object' && !Array.isArray(problem.errors)
    ? Object.values(problem.errors).flat() : [];
  for (const value of [problem.detail, ...errors, problem.message, problem.title])
    if (typeof value === 'string' && value.trim()) return value.trim();
  return fallback;
}

export async function readApiError(
  response: Response,
  fallback: string,
  readJson: (response: Response) => Promise<unknown> = response => response.json(),
): Promise<string> {
  try {
    return apiErrorMessage(await readJson(response), fallback);
  } catch (error) {
    if (isAbortError(error)) throw error;
    return fallback;
  }
}
