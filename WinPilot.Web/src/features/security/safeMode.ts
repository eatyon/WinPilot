import { readonly, ref } from 'vue'
import { apiFetch } from '../../api/client'
import { connectionRestoreVersion, markConnectionLostUnlessRestored, markConnectionRestored } from '../connection/connectionStatus'

const enabled = ref(false)
let loaded = false
let loading: Promise<void> | undefined
let events: EventSource | undefined
let syncCheckGeneration = 0
let stateVersion = 0

export const safeModeEnabled = readonly(enabled)
export const safeModeMessage = '安全模式下不可用，请通过本机托盘关闭安全模式'

export async function loadSafeMode(): Promise<void> {
  if (loaded) return
  if (loading) return loading
  const version = stateVersion
  loading = (async () => {
    try {
      const response = await apiFetch('/api/security/safe-mode', { cache: 'no-store' })
      if (response.ok) {
        const state = await response.json() as { enabled?: boolean }
        if (version !== stateVersion || typeof state.enabled !== 'boolean') return
        enabled.value = state.enabled
        loaded = true
      }
    } finally { loading = undefined }
  })()
  return loading
}

export function startSafeModeSync() {
  void loadSafeMode().catch(() => {})
  if (events) return
  const source = new EventSource('/api/security/safe-mode/events')
  events = source
  source.onmessage = event => {
    if (events !== source) return
    try {
      const state = JSON.parse(event.data) as { enabled?: boolean }
      if (typeof state.enabled !== 'boolean') return
      enabled.value = state.enabled
      stateVersion++
      loaded = true
      syncCheckGeneration++
      markConnectionRestored()
    } catch { return }
  }
  source.onerror = () => { if (events === source) void handleSyncError(source) }
}

async function handleSyncError(source: EventSource): Promise<void> {
  const generation = ++syncCheckGeneration
  const restoredBeforeProbe = connectionRestoreVersion()
  let response: Response
  try {
    response = await fetch('/api/security/safe-mode', { cache: 'no-store', signal: AbortSignal.timeout(1500) })
  } catch {
    if (events === source && generation === syncCheckGeneration) markConnectionLostUnlessRestored(restoredBeforeProbe)
    return
  }
  if (events !== source || generation !== syncCheckGeneration) return
  markConnectionRestored()
  if (response.status === 401) {
    window.dispatchEvent(new Event('winpilot:session-expired'))
  } else if (response.ok) {
    const state = await response.json().catch(() => undefined) as { enabled?: boolean } | undefined
    if (events !== source || generation !== syncCheckGeneration) return
    if (typeof state?.enabled === 'boolean') {
      enabled.value = state.enabled
      stateVersion++
      loaded = true
    }
  }
}

export function stopSafeModeSync() {
  stateVersion++
  syncCheckGeneration++
  events?.close()
  events = undefined
}
