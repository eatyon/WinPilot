import { readonly, ref } from 'vue'

export type ConnectionStatus = 'online' | 'disconnected'

const status = ref<ConnectionStatus>('online')
let restoreVersion = 0
export const connectionStatus = readonly(status)

export function connectionRestoreVersion(): number {
  return restoreVersion
}

export function markConnectionLost(): void {
  status.value = 'disconnected'
}

export function markConnectionLostUnlessRestored(since: number): void {
  if (restoreVersion === since) markConnectionLost()
}

export function markConnectionRestored(): void {
  restoreVersion++
  status.value = 'online'
}
