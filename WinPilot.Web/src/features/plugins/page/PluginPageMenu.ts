import type { PluginPageScope } from './PluginPageTypes';

export type PluginMenuOptions = {
  width?: number;
  maxHeight?: number;
  beforeOpen?: () => boolean | void;
};

export function placePluginMenu(trigger: HTMLElement, menu: HTMLElement, content: HTMLElement, maxHeight: number, width = trigger.getBoundingClientRect().width) {
  const rect = trigger.getBoundingClientRect();
  const below = window.innerHeight - rect.bottom - 8;
  const above = rect.top - 8;
  const menuWidth = Math.min(width, window.innerWidth - 8);
  menu.style.width = `${menuWidth}px`;
  const required = Math.min(content.scrollHeight + 2, maxHeight);
  const upwards = below < required && above > below;
  const height = Math.min(maxHeight, Math.max(40, upwards ? above : below));
  menu.style.left = `${Math.max(4, Math.min(rect.left, window.innerWidth - menuWidth - 4))}px`;
  menu.style.maxHeight = `${height}px`;
  menu.style.setProperty('--app-menu-max-height', `${height}px`);
  menu.style.top = `${upwards ? Math.max(4, rect.top - menu.offsetHeight - 4) : rect.bottom + 4}px`;
}

export function enhancePluginMenu(trigger: HTMLButtonElement, content: HTMLElement, scope: Pick<PluginPageScope, 'signal' | 'onDispose'>, options: PluginMenuOptions = {}) {
  const document = trigger.ownerDocument;
  const events = new AbortController();
  const menu = document.createElement('div');
  const previousHidden = content.hidden;
  const previousExpanded = trigger.getAttribute('aria-expanded');
  const previousHasPopup = trigger.getAttribute('aria-haspopup');
  const previousRole = content.getAttribute('role');
  const previousLabel = content.getAttribute('aria-label');
  const previousOptionsClass = content.classList.contains('app-select-options');
  let activeEvents: AbortController | undefined;
  let disposed = false;
  menu.className = 'app-select-menu app-action-menu';
  menu.hidden = true;
  content.before(menu);
  menu.append(content);
  content.hidden = false;
  content.classList.add('app-select-options');
  content.setAttribute('role', 'menu');
  content.setAttribute('aria-label', trigger.getAttribute('aria-label') || trigger.textContent?.trim() || '操作菜单');
  if (typeof menu.showPopover === 'function') menu.setAttribute('popover', 'manual');
  trigger.setAttribute('aria-haspopup', 'menu');
  trigger.setAttribute('aria-expanded', 'false');

  const items = () => [...content.querySelectorAll<HTMLButtonElement>('button:not(:disabled)')]
    .filter(item => item.getClientRects().length > 0 && getComputedStyle(item).visibility !== 'hidden');
  const position = () => {
    if (!menu.hidden) placePluginMenu(trigger, menu, content, options.maxHeight ?? 320, options.width ?? 190);
  };
  const observer = new MutationObserver(position);
  function close(restoreFocus = false) {
    observer.disconnect();
    activeEvents?.abort();
    activeEvents = undefined;
    if (typeof menu.hidePopover === 'function' && menu.matches(':popover-open')) menu.hidePopover();
    menu.hidden = true;
    trigger.setAttribute('aria-expanded', 'false');
    if (restoreFocus && trigger.isConnected) trigger.focus({ preventScroll: true });
  }
  function open() {
    if (disposed || scope.signal.aborted || trigger.matches(':disabled') || !menu.hidden) return;
    if (options.beforeOpen?.() === false) return;
    menu.hidden = false;
    menu.classList.add('is-positioned');
    if (typeof menu.showPopover === 'function') menu.showPopover();
    trigger.setAttribute('aria-expanded', 'true');
    position();
    observer.observe(content, { childList: true, subtree: true, attributes: true, characterData: true });
    activeEvents?.abort();
    activeEvents = new AbortController();
    document.addEventListener('pointerdown', event => {
      if (!event.composedPath().includes(trigger) && !event.composedPath().includes(menu)) close();
    }, { signal: activeEvents.signal });
    window.addEventListener('resize', position, { signal: activeEvents.signal });
    window.addEventListener('scroll', position, { signal: activeEvents.signal, capture: true });
  }
  function onKeyDown(event: KeyboardEvent) {
    if (event.key === 'Escape' && !menu.hidden) {
      event.preventDefault();
      event.stopPropagation();
      close(true);
      return;
    }
    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp' && event.key !== 'Home' && event.key !== 'End') return;
    event.preventDefault();
    if (menu.hidden) open();
    if (menu.hidden) return;
    const controls = items();
    const focused = (trigger.getRootNode() as Document | ShadowRoot).activeElement;
    const current = controls.indexOf(focused as HTMLButtonElement);
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? controls.length - 1
      : current < 0 ? event.key === 'ArrowUp' ? controls.length - 1 : 0
      : (current + (event.key === 'ArrowUp' ? -1 : 1) + controls.length) % controls.length;
    controls[next]?.focus({ preventScroll: true });
    controls[next]?.scrollIntoView({ block: 'nearest' });
  }
  trigger.addEventListener('click', () => { if (menu.hidden) open(); else close(); }, { signal: events.signal });
  trigger.addEventListener('keydown', onKeyDown, { signal: events.signal });
  menu.addEventListener('keydown', onKeyDown, { signal: events.signal });
  menu.addEventListener('click', event => {
    const item = event.target instanceof Element ? event.target.closest<HTMLButtonElement>('button') : null;
    if (item && !item.disabled && content.contains(item)) close(true);
  }, { signal: events.signal, capture: true });
  trigger.addEventListener('keydown', event => {
    if (event.key !== 'Tab' || event.shiftKey || menu.hidden) return;
    const first = items()[0];
    if (!first) return;
    event.preventDefault();
    event.stopPropagation();
    first.focus({ preventScroll: true });
  }, { signal: events.signal });
  menu.addEventListener('keydown', event => {
    if (event.key !== 'Tab') return;
    const controls = items();
    const focused = (trigger.getRootNode() as Document | ShadowRoot).activeElement;
    const index = controls.indexOf(focused as HTMLButtonElement);
    if (index < 0) return;
    let target: HTMLElement | undefined = event.shiftKey ? controls[index - 1] || trigger : controls[index + 1];
    if (!target) {
      const root = trigger.closest('dialog[open], [role="dialog"][aria-modal="true"]') || trigger.getRootNode() as Document | ShadowRoot;
      const siblings = [...root.querySelectorAll<HTMLElement>('a[href], button, input, select, textarea, [tabindex], [contenteditable="true"]')]
        .filter(item => item.tabIndex >= 0 && !item.matches(':disabled') && !item.closest('[inert]') &&
          !menu.contains(item) && item.getClientRects().length > 0 && getComputedStyle(item).visibility !== 'hidden');
      target = siblings[siblings.indexOf(trigger) + 1];
      close();
    } else if (target === trigger) close();
    event.preventDefault();
    event.stopPropagation();
    (target || trigger).focus({ preventScroll: true });
  }, { signal: events.signal });
  function dispose() {
    if (disposed) return;
    disposed = true;
    close();
    events.abort();
    if (!previousOptionsClass) content.classList.remove('app-select-options');
    content.hidden = previousHidden;
    if (previousRole === null) content.removeAttribute('role'); else content.setAttribute('role', previousRole);
    if (previousLabel === null) content.removeAttribute('aria-label'); else content.setAttribute('aria-label', previousLabel);
    if (previousExpanded === null) trigger.removeAttribute('aria-expanded'); else trigger.setAttribute('aria-expanded', previousExpanded);
    if (previousHasPopup === null) trigger.removeAttribute('aria-haspopup'); else trigger.setAttribute('aria-haspopup', previousHasPopup);
    menu.replaceWith(content);
  }
  scope.onDispose(dispose);
  return { open, close, dispose };
}
