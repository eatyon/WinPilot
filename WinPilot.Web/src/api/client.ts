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
  return error instanceof Error ? error.message : fallback
}
