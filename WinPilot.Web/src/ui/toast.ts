export type ToastType = 'success' | 'error'

export function showToast(message: string, type: ToastType = 'error'): void
{
  if (!message) return
  window.dispatchEvent(new CustomEvent('winpilot:toast', { detail: { message, type } }))
}
