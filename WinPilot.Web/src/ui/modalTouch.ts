import { getModalInteractionRoot } from './modalFocus.ts';

const modals: HTMLElement[] = [];

export function manageModalTouch(modal: HTMLElement): () => void {
  const document = modal.ownerDocument;
  const view = document.defaultView;
  modals.push(modal);
  let position: { x: number; y: number } | undefined;

  function onStart(event: TouchEvent) {
    position = undefined;
    if (modals.at(-1) !== modal) return;
    const touch = event.touches.length === 1 ? event.touches[0] : undefined;
    position = touch ? { x: touch.clientX, y: touch.clientY } : undefined;
  }

  function onMove(event: TouchEvent) {
    if (modals.at(-1) !== modal || !view) return;
    const touch = event.touches.length === 1 ? event.touches[0] : undefined;
    if (!touch || !position) { position = undefined; return; }
    const dx = position.x - touch.clientX;
    const dy = position.y - touch.clientY;
    position = { x: touch.clientX, y: touch.clientY };
    if (!dx && !dy) return;
    const path = event.composedPath();
    const boundary = getModalInteractionRoot(modal, path);
    const vertical = Math.abs(dy) >= Math.abs(dx);
    const delta = vertical ? dy : dx;
    if (boundary) {
      for (const target of path) {
        if (target instanceof view.HTMLElement) {
          if (!vertical && target.matches('input[type="range"]')) return;
          const style = view.getComputedStyle(target);
          const overflow = vertical ? style.overflowY : style.overflowX;
          const range = vertical ? target.scrollHeight - target.clientHeight : target.scrollWidth - target.clientWidth;
          const offset = vertical ? target.scrollTop : target.scrollLeft;
          if (/^(auto|scroll)$/.test(overflow) && range > 0 && (delta > 0 ? offset < range - 1 : offset > 0)) return;
        }
        if (target === boundary) break;
      }
    }
    if (event.cancelable) event.preventDefault();
  }

  document.addEventListener('touchstart', onStart, { passive: true, capture: true });
  document.addEventListener('touchmove', onMove, { passive: false, capture: true });
  let released = false;
  return () => {
    if (released) return;
    released = true;
    document.removeEventListener('touchstart', onStart, true);
    document.removeEventListener('touchmove', onMove, true);
    const index = modals.lastIndexOf(modal);
    if (index >= 0) modals.splice(index, 1);
  };
}
