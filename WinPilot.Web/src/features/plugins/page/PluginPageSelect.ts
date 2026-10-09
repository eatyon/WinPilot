import type { PluginPageScope } from './PluginPageTypes';
import { placePluginMenu } from './PluginPageMenu.ts';
type SelectInstance = {
  select: HTMLSelectElement; root: HTMLElement; trigger: HTMLButtonElement;
  valueText: HTMLElement; menu: HTMLDivElement; options: HTMLDivElement; tabIndex: number; ariaHidden: string | null;
  wrapped: boolean; observer: MutationObserver; events: AbortController;
};
export function enhancePluginSelect(root: ShadowRoot | HTMLElement, scope: Pick<PluginPageScope, 'signal' | 'onDispose'>) {
  const document = root.ownerDocument
  const events = new AbortController()
  let disposed = false
  const dispose = () => {
    if (disposed) return
    disposed = true
    observer.disconnect()
    close()
    events.abort()
    for (const instance of mounted) release(instance)
    mounted.clear()
  }
  const instances = new WeakMap<HTMLSelectElement, SelectInstance>()
  const mounted = new Set<SelectInstance>()
  let active: SelectInstance | null = null

  function close(select?: HTMLSelectElement) {
    if (!active || (select && active.select !== select)) return
    if (typeof active.menu.hidePopover === 'function' && active.menu.matches(':popover-open')) active.menu.hidePopover()
    active.menu.hidden = true
    active.menu.remove()
    active.trigger.setAttribute('aria-expanded', 'false')
    active = null
  }

  function release(instance: SelectInstance) {
    close(instance.select)
    instance.observer.disconnect()
    instance.events.abort()
    instance.trigger.remove()
    instance.menu.remove()
    instance.select.classList.remove('app-select-native')
    instance.select.tabIndex = instance.tabIndex
    if (instance.ariaHidden === null) instance.select.removeAttribute('aria-hidden')
    else instance.select.setAttribute('aria-hidden', instance.ariaHidden)
    instance.root.classList.remove('wp-select', 'app-select', 'app-multi-select')
    if (instance.wrapped) instance.root.replaceWith(instance.select)
    mounted.delete(instance)
    instances.delete(instance.select)
  }

  function place(instance: SelectInstance) {
    placePluginMenu(instance.trigger, instance.menu, instance.options, 240)
  }

  function refresh(select: HTMLSelectElement) {
    if (disposed) return
    const instance = instances.get(select)
    if (!instance) return
    const selected = select.selectedOptions
    instance.valueText.textContent = select.multiple
      ? selected.length ? `已选择 ${selected.length} 项` : '请选择'
      : selected[0]?.textContent || '请选择'
    instance.trigger.disabled = select.matches(':disabled') || select.options.length === 0
    const focused = (instance.trigger.getRootNode() as ShadowRoot | Document).activeElement as HTMLElement | null
    const focusedIndex = focused && instance.options.contains(focused) ? focused.dataset.index : undefined
    const scrollTop = instance.options.scrollTop
    instance.options.replaceChildren(...[...select.options].map((option, index) => {
      const disabled = option.disabled || (option.parentElement instanceof HTMLOptGroupElement && option.parentElement.disabled)
      if (select.multiple) {
        const label = document.createElement('label')
        label.className = 'app-select-option app-multi-option'
        label.classList.toggle('is-disabled', disabled)
        const input = document.createElement('input')
        input.type = 'checkbox'
        input.className = 'app-multi-input'
        input.dataset.index = String(index)
        input.checked = option.selected
        input.disabled = disabled
        const text = document.createElement('span')
        text.textContent = option.textContent
        label.append(input, text)
        return label
      }
      const item = document.createElement('button')
      item.type = 'button'
      item.className = 'app-select-option'
      item.dataset.value = option.value
      item.dataset.index = String(index)
      item.setAttribute('role', 'option')
      item.setAttribute('aria-selected', String(option.selected))
      item.disabled = disabled
      item.textContent = option.textContent
      return item
    }))
    instance.options.scrollTop = scrollTop
    if (focusedIndex !== undefined) instance.options.querySelector<HTMLElement>(`[data-index="${focusedIndex}"]`)?.focus({ preventScroll: true })
    if (active === instance && instance.trigger.disabled) close()
    else if (active === instance) place(instance)
  }

  function open(instance: SelectInstance) {
    if (disposed || scope.signal.aborted) return
    refresh(instance.select)
    if (instance.trigger.disabled) return
    close()
    const menuHost = instance.root
    menuHost.append(instance.menu)
    instance.menu.hidden = false
    instance.menu.classList.add('is-positioned')
    instance.trigger.setAttribute('aria-expanded', 'true')
    active = instance
    if (typeof instance.menu.showPopover === 'function') instance.menu.showPopover()
    place(instance)
    const selectedItem = instance.menu.querySelector<HTMLElement>('[aria-selected="true"], .app-multi-input:checked')
    if (selectedItem) {
      const top = selectedItem.getBoundingClientRect().top - instance.options.getBoundingClientRect().top + instance.options.scrollTop
      if (top < instance.options.scrollTop) instance.options.scrollTop = top
      else if (top + selectedItem.offsetHeight > instance.options.scrollTop + instance.options.clientHeight) {
        instance.options.scrollTop = top + selectedItem.offsetHeight - instance.options.clientHeight
      }
    }
  }

  function handleTab(event: KeyboardEvent, instance: SelectInstance) {
    if (event.key !== 'Tab' || active !== instance) return
    const items = optionControls(instance)
    const focused = (instance.trigger.getRootNode() as ShadowRoot | Document).activeElement
    let target
    if (focused === instance.trigger) {
      if (event.shiftKey || !items.length) { close(); return }
      target = items[0]
    } else {
      const index = items.indexOf(focused as HTMLElement)
      if (index < 0) return
      target = event.shiftKey ? (items[index - 1] || instance.trigger) : items[index + 1]
      if (!target) {
        const scope = instance.trigger.closest('dialog[open], [role="dialog"][aria-modal="true"]') || root
        const controls = [...scope.querySelectorAll<HTMLElement>('a[href], button, input, select, textarea, [tabindex], [contenteditable="true"]')]
          .filter(element => element.tabIndex >= 0 && !element.matches(':disabled') && !element.closest('[inert]') &&
            !instance.menu.contains(element) && element.getClientRects().length > 0 && getComputedStyle(element).visibility !== 'hidden')
        target = controls[controls.indexOf(instance.trigger) + 1]
        close()
        if (!target) { instance.trigger.focus({ preventScroll: true }); return }
      }
    }
    event.preventDefault()
    event.stopPropagation()
    target.focus({ preventScroll: true })
  }

  function enhance(select: HTMLSelectElement) {
    if (disposed || instances.has(select)) return
    const existingControl = select.parentElement?.classList.contains('select-control')
    const root = existingControl ? select.parentElement! : document.createElement('span')
    const tabIndex = select.tabIndex
    const ariaHidden = select.getAttribute('aria-hidden')
    if (!existingControl) { select.before(root); root.append(select) }
    root.classList.add('app-select')
    root.classList.add('wp-select')
    root.classList.toggle('app-multi-select', select.multiple)
    const trigger = document.createElement('button')
    trigger.type = 'button'
    trigger.className = 'app-select-trigger'
    trigger.setAttribute('aria-label', select.getAttribute('aria-label') || select.labels?.[0]?.textContent?.trim() || select.id || '选择选项')
    if (!select.multiple) trigger.setAttribute('aria-haspopup', 'listbox')
    trigger.setAttribute('aria-expanded', 'false')
    const valueText = document.createElement('span')
    const chevron = document.createElement('span')
    chevron.className = 'app-select-chevron'
    chevron.setAttribute('aria-hidden', 'true')
    trigger.append(valueText, chevron)
    const menu = document.createElement('div')
    menu.className = 'app-select-menu'
    const options = document.createElement('div')
    options.className = 'app-select-options'
    menu.append(options)
    if (typeof menu.showPopover === 'function') menu.setAttribute('popover', 'manual')
    menu.setAttribute('role', select.multiple ? 'group' : 'listbox')
    menu.setAttribute('aria-label', trigger.getAttribute('aria-label')!)
    menu.hidden = true
    root.append(trigger, menu)
    select.classList.add('app-select-native')
    select.tabIndex = -1
    select.setAttribute('aria-hidden', 'true')
    const instance: SelectInstance = { select, root, trigger, valueText, menu, options, tabIndex, ariaHidden, wrapped: !existingControl, observer: new MutationObserver(() => refresh(select)), events: new AbortController() }
    instances.set(select, instance)
    const observer = instance.observer
    mounted.add(instance)
    observer.observe(select, { childList: true, subtree: true, attributes: true, characterData: true })
    select.addEventListener('change', () => refresh(select), { signal: instance.events.signal })
    select.addEventListener('focus', () => trigger.focus({ preventScroll: true }), { signal: instance.events.signal })
    select.form?.addEventListener('reset', () => queueMicrotask(() => refresh(select)), { signal: instance.events.signal })
    trigger.addEventListener('click', () => {
      if (active === instance) { close(); return }
      if (!select.dispatchEvent(new Event('app-select-beforeopen', { cancelable: true }))) return
      open(instance)
    })
    trigger.addEventListener('keydown', event => {
      handleTab(event, instance)
      if (event.key === 'Escape') close()
      if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return
      event.preventDefault()
      if (active !== instance) trigger.click()
      if (active === instance) {
        const selected = menu.querySelector<HTMLElement>('[aria-selected="true"]:not(:disabled), .app-multi-input:checked:not(:disabled)')
        const target = selected ?? optionControls(instance)[0]
        target?.focus()
      }
    })
    menu.addEventListener('click', event => {
      if (select.multiple) return
      const item = event.target instanceof Element ? event.target.closest<HTMLButtonElement>('.app-select-option') : null
      if (!item || item.disabled || !menu.contains(item)) return
      select.selectedIndex = Number(item.dataset.index)
      select.dispatchEvent(new Event('change', { bubbles: true }))
      close()
      trigger.focus({ preventScroll: true })
    })
    menu.addEventListener('change', event => {
      const input = event.target
      if (!select.multiple || !(input instanceof HTMLInputElement) || input.disabled) return
      const option = select.options[Number(input.dataset.index)]
      if (!option) return
      option.selected = input.checked
      select.dispatchEvent(new Event('change', { bubbles: true }))
    })
    menu.addEventListener('keydown', event => {
      handleTab(event, instance)
      if (event.key === 'Escape') { close(); trigger.focus({ preventScroll: true }); return }
      if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return
      event.preventDefault()
      const items = optionControls(instance)
      const index = items.indexOf((instance.trigger.getRootNode() as ShadowRoot | Document).activeElement as HTMLElement)
      items[(index + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length]?.focus()
    })
    refresh(select)
  }

  function optionControls(instance: SelectInstance) {
    return [...instance.options.querySelectorAll<HTMLElement>('button:not(:disabled), input[type="checkbox"]:not(:disabled)')]
  }

  root.querySelectorAll<HTMLSelectElement>('select').forEach(enhance)
  const observer = new MutationObserver(records => {
    for (const record of records) {
      for (const node of record.addedNodes) {
        if (!(node instanceof Element)) continue
        if (node instanceof HTMLSelectElement) enhance(node)
        node.querySelectorAll<HTMLSelectElement>('select').forEach(enhance)
      }
    }
    for (const instance of mounted) {
      if (root.contains(instance.select)) continue
      release(instance)
    }
  })
  observer.observe(root, { childList: true, subtree: true })
  document.addEventListener('pointerdown', event => {
    if (active && !event.composedPath().includes(active.root) && !event.composedPath().includes(active.menu)) close()
  }, { signal: events.signal })
  root.addEventListener('keydown', event => {
    const keyEvent = event as KeyboardEvent
    if (keyEvent.key === 'Escape' && active) {
      const trigger = active.trigger
      close()
      trigger.focus({ preventScroll: true })
      keyEvent.preventDefault()
      keyEvent.stopPropagation()
    }
  }, { signal: events.signal, capture: true })
  window.addEventListener('resize', () => { if (active) place(active) }, { signal: events.signal })
  window.addEventListener('scroll', () => { if (active) place(active) }, { capture: true, signal: events.signal })
  const api = { refresh, dispose, open(select: HTMLSelectElement) { const instance = instances.get(select); if (instance) open(instance) }, close }
  scope.onDispose(dispose)
  return api
}
