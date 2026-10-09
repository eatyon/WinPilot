import { nextTick, onBeforeUnmount, ref, type Ref } from 'vue'
import { registerModalPortal } from '../ui/modalFocus'

export function useAnchoredMenu(root: Ref<HTMLElement | undefined>, maxMenuHeight = 220) {
  const trigger = ref<HTMLButtonElement>()
  const menu = ref<HTMLElement>()
  const open = ref(false)
  const positioned = ref(false)
  const menuStyle = ref<Record<string, string>>({})
  let releasePortal: (() => void) | undefined

  function place() {
    if (!open.value || !trigger.value || !menu.value) return
    const rect = trigger.value.getBoundingClientRect()
    const below = window.innerHeight - rect.bottom - 6
    const above = rect.top - 6
    const content = menu.value.querySelector<HTMLElement>('.app-select-options') ?? menu.value
    const height = Math.min(content.scrollHeight, maxMenuHeight)
    const upwards = below < height && above > below
    const maxHeight = Math.min(maxMenuHeight, Math.max(40, upwards ? above : below))
    const width = Math.min(rect.width, window.innerWidth - 8)
    menuStyle.value = {
      left: `${Math.max(4, Math.min(rect.left, window.innerWidth - width - 4))}px`,
      top: `${upwards ? Math.max(4, rect.top - Math.min(height, maxHeight) - 4) : rect.bottom + 4}px`,
      width: `${width}px`,
      maxHeight: `${maxHeight}px`,
      '--app-menu-max-height': `${maxHeight}px`
    }
    positioned.value = true
  }

  function close() {
    if (!open.value) return
    open.value = false
    releasePortal?.()
    releasePortal = undefined
    positioned.value = false
    document.removeEventListener('pointerdown', onPointerDown)
    document.removeEventListener('focusin', onFocusIn)
    document.removeEventListener('keydown', onTab, true)
    window.removeEventListener('scroll', place, true)
    window.removeEventListener('resize', place)
  }

  async function toggle() {
    if (open.value) { close(); return }
    open.value = true
    positioned.value = false
    document.addEventListener('pointerdown', onPointerDown)
    document.addEventListener('focusin', onFocusIn)
    document.addEventListener('keydown', onTab, true)
    window.addEventListener('scroll', place, true)
    window.addEventListener('resize', place)
    await nextTick()
    if (!open.value) return
    if (menu.value && root.value) releasePortal = registerModalPortal(menu.value, root.value)
    place()
  }

  function onPointerDown(event: PointerEvent) {
    const target = event.target as Node
    if (!root.value?.contains(target) && !menu.value?.contains(target)) close()
  }

  function onFocusIn(event: FocusEvent) {
    const target = event.target as Node
    if (!root.value?.contains(target) && !menu.value?.contains(target)) close()
  }

  function onTab(event: KeyboardEvent) {
    if (event.key !== 'Tab' || !open.value || !menu.value || !trigger.value) return
    const items = [...menu.value.querySelectorAll<HTMLElement>('button:not(:disabled), input[type="checkbox"]:not(:disabled)')]
      .filter(item => item.tabIndex >= 0 && item.getClientRects().length > 0 && getComputedStyle(item).visibility !== 'hidden')
    const active = document.activeElement
    let target: HTMLElement | undefined
    if (active === trigger.value && !event.shiftKey) target = items[0]
    else if (menu.value.contains(active)) {
      const index = items.indexOf(active as HTMLElement)
      if (index < 0) return
      target = event.shiftKey ? (items[index - 1] ?? trigger.value) : items[index + 1]
      if (!target) {
        close()
        trigger.value.focus({ preventScroll: true })
        return
      }
    }
    if (!target) return
    event.preventDefault()
    event.stopPropagation()
    target.focus({ preventScroll: true })
  }

  onBeforeUnmount(close)
  return { trigger, menu, open, positioned, menuStyle, place, close, toggle }
}
