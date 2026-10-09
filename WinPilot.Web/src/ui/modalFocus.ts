const modals: Array<{ element: HTMLElement; previousFocus?: HTMLElement }> = [];
const portals = new Map<HTMLElement, HTMLElement>();
const focusableSelector = 'a[href], button, input, select, textarea, [tabindex], [contenteditable="true"]';

function activeElement(): Element | null {
  let active = document.activeElement;
  while (active?.shadowRoot?.activeElement) active = active.shadowRoot.activeElement;
  return active;
}

function composedParent(element: HTMLElement): HTMLElement | null {
  if (element.parentElement) return element.parentElement;
  const root = element.getRootNode();
  return root instanceof ShadowRoot && root.host instanceof HTMLElement ? root.host : null;
}

function composedContains(container: HTMLElement, element: HTMLElement): boolean {
  for (let current: HTMLElement | null = element; current; current = composedParent(current)) {
    if (current === container) return true;
  }
  return false;
}

function focusableElements(root: HTMLElement | ShadowRoot): HTMLElement[] {
  const elements: HTMLElement[] = [];
  for (const child of root.children) {
    if (!(child instanceof HTMLElement) || child.matches('[inert]')) continue;
    if (child.matches(focusableSelector) && child.tabIndex >= 0 && available(child)) elements.push(child);
    elements.push(...focusableElements(child.shadowRoot ?? child));
  }
  return elements;
}

function available(element: HTMLElement): boolean {
  for (let current: HTMLElement | null = element; current; current = composedParent(current)) {
    if (current.hasAttribute('inert')) return false;
  }
  return !element.matches(':disabled') && element.getClientRects().length > 0 && getComputedStyle(element).visibility !== 'hidden';
}

export function registerModalPortal(portal: HTMLElement, owner: HTMLElement): () => void {
  portals.set(portal, owner);
  return () => portals.delete(portal);
}

export function getModalInteractionRoot(modal: HTMLElement, path: EventTarget[]): HTMLElement | null {
  if (path.includes(modal)) return modal;
  for (const [portal, owner] of portals) {
    if (path.includes(portal) && composedContains(modal, owner)) return portal;
  }
  return null;
}

export function manageModalFocus(modal: HTMLElement): () => void {
  const active = activeElement();
  const state = { element: modal, previousFocus: active instanceof HTMLElement ? active : undefined };
  modals.push(state);
  const autofocus = focusableElements(modal).find(element => element.hasAttribute('autofocus'));
  (autofocus && available(autofocus) ? autofocus : modal).focus({ preventScroll: true });

  function onKeyDown(event: KeyboardEvent) {
    if (event.key !== 'Tab' || modals.at(-1) !== state || !available(modal)) return;
    const elements = focusableElements(modal);
    for (const [portal, owner] of portals) {
      if (!composedContains(modal, owner)) continue;
      let ownerIndex = elements.length - 1;
      while (ownerIndex >= 0 && !composedContains(owner, elements[ownerIndex]!)) ownerIndex--;
      if (ownerIndex < 0) continue;
      const options = focusableElements(portal);
      elements.splice(ownerIndex + 1, 0, ...options);
    }
    const current = elements.indexOf(activeElement() as HTMLElement);
    const index = event.shiftKey
      ? (current <= 0 ? elements.length - 1 : current - 1)
      : (current + 1) % elements.length;
    event.preventDefault();
    (elements[index] ?? modal).focus({ preventScroll: true });
  }

  function onFocusIn() {
    if (modals.at(-1) !== state || !available(modal)) return;
    const active = activeElement();
    if (!(active instanceof HTMLElement) || composedContains(modal, active)) return;
    for (const [portal, owner] of portals) {
      if (composedContains(modal, owner) && composedContains(portal, active)) return;
    }
    modal.focus({ preventScroll: true });
  }

  document.addEventListener('keydown', onKeyDown);
  document.addEventListener('focusin', onFocusIn);
  return () => {
    document.removeEventListener('keydown', onKeyDown);
    document.removeEventListener('focusin', onFocusIn);
    const index = modals.indexOf(state);
    if (index < 0) return;
    const wasTop = index === modals.length - 1;
    for (const child of modals.slice(index + 1)) {
      if (child.previousFocus && composedContains(modal, child.previousFocus)) child.previousFocus = state.previousFocus;
    }
    if (index >= 0) modals.splice(index, 1);
    if (!wasTop) return;
    const parent = modals.at(-1)?.element;
    const previousFocus = state.previousFocus;
    if (previousFocus?.isConnected && available(previousFocus) && (!parent || composedContains(parent, previousFocus))) {
      previousFocus.focus({ preventScroll: true });
    } else if (parent && available(parent)) {
      parent.focus({ preventScroll: true });
    }
  };
}
