import { connectionRestoreVersion, markConnectionLostUnlessRestored, markConnectionRestored } from '../features/connection/connectionStatus'

const connectionErrorMessage = '无法连接到 WinPilot'

export class WinPilotConnectionError extends Error
{
  constructor() { super(connectionErrorMessage) }
}

export async function apiFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response>
{
  const restoredBeforeRequest = connectionRestoreVersion()
  try {
    const response = await fetch(input, init)
    markConnectionRestored()
    return response
  }
  catch (error)
  {
    if (isAbortError(error)) throw error
    const connectionError = new WinPilotConnectionError()
    markConnectionLostUnlessRestored(restoredBeforeRequest)
    throw connectionError
  }
}

export function isAbortError(error: unknown): boolean
{
  return error instanceof DOMException && error.name === 'AbortError'
}

export function userErrorMessage(error: unknown, fallback: string): string
{
  if (error instanceof WinPilotConnectionError) return ''
  return error instanceof Error && error.message.trim() ? error.message : fallback
}

export function apiErrorMessage(body: unknown, fallback: string): string
{
  if (!body || typeof body !== 'object' || Array.isArray(body)) return fallback
  const problem = body as Record<string, unknown>
  const errors = problem.errors && typeof problem.errors === 'object' && !Array.isArray(problem.errors)
    ? Object.values(problem.errors).flat() : []
  for (const value of [problem.detail, ...errors, problem.message, problem.title])
    if (typeof value === 'string' && value.trim()) return value.trim()
  return fallback
}

export async function readApiError(response: Response, fallback: string,
  readJson: (response: Response) => Promise<unknown> = response => response.json()): Promise<string>
{
  try { return apiErrorMessage(await readJson(response), fallback) }
  catch (error) {
    if (isAbortError(error)) throw error
    return fallback
  }
}
