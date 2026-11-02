const modals: Array<{ element: HTMLElement; previousFocus?: HTMLElement }> = [];
const focusableSelector = 'a[href], button, input, select, textarea, [tabindex], [contenteditable="true"]';

function activeElement(): Element | null {
  let active = document.activeElement;
  while (active?.shadowRoot?.activeElement) active = active.shadowRoot.activeElement;
  return active;
}

// 插件控件可能位于 Shadow DOM，检查焦点归属时需继续沿宿主元素向外遍历。
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

export function getModalInteractionRoot(modal: HTMLElement, path: EventTarget[]): HTMLElement | null {
  return path.includes(modal) ? modal : null;
}

// 只由最上层弹窗约束焦点；关闭后恢复到仍可用的原焦点或下层弹窗。
export function manageModalFocus(modal: HTMLElement): () => void {
  const active = activeElement();
  const state = { element: modal, previousFocus: active instanceof HTMLElement ? active : undefined };
  modals.push(state);
  const autofocus = focusableElements(modal).find(element => element.hasAttribute('autofocus'));
  (autofocus && available(autofocus) ? autofocus : modal).focus({ preventScroll: true });

  function onKeyDown(event: KeyboardEvent) {
    if (event.key !== 'Tab' || modals.at(-1) !== state || !available(modal)) return;
    const elements = focusableElements(modal);
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
