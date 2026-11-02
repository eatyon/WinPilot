// 将超过移动阈值的单指拖动转为预览组件已有的鼠标拖动，多指操作交回组件处理。
export function bindImagePreviewTouchDrag(container: HTMLElement): () => void {
  const document = container.ownerDocument;
  const view = document.defaultView;
  if (!view) return () => {};
  const MouseEvent = view.MouseEvent;
  const dragThreshold = 6;
  let drag: { image: HTMLImageElement; identifier: number; x: number; y: number; started: boolean } | undefined;

  function finish() {
    if (!drag) return;
    const { x, y, started } = drag;
    drag = undefined;
    if (started) document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, clientX: x, clientY: y, button: 0, buttons: 0 }));
  }

  function onStart(event: TouchEvent) {
    if (event.touches.length !== 1) { finish(); return; }
    if (drag) return;
    const image = container.querySelector<HTMLImageElement>('img.n-image-preview');
    const touch = event.touches[0];
    if (!image || event.target !== image || !touch) return;
    drag = { image, identifier: touch.identifier, x: touch.clientX, y: touch.clientY, started: false };
  }

  function onMove(event: TouchEvent) {
    if (!drag) return;
    const identifier = drag.identifier;
    const touch = Array.from(event.touches).find(touch => touch.identifier === identifier);
    if (event.touches.length !== 1 || !touch) { finish(); return; }
    if (!drag.started) {
      if (Math.hypot(touch.clientX - drag.x, touch.clientY - drag.y) < dragThreshold) return;
      if (!event.cancelable) { finish(); return; }
      drag.started = true;
      drag.image.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, clientX: drag.x, clientY: drag.y, button: 0, buttons: 1 }));
    }
    if (event.cancelable) event.preventDefault();
    drag.x = touch.clientX;
    drag.y = touch.clientY;
    document.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: drag.x, clientY: drag.y, button: 0, buttons: 1 }));
  }

  function onEnd(event: TouchEvent) {
    if (!drag) return;
    const identifier = drag.identifier;
    const touch = Array.from(event.changedTouches).find(touch => touch.identifier === identifier);
    if (!touch) return;
    drag.x = touch.clientX;
    drag.y = touch.clientY;
    if (drag.started && event.cancelable) event.preventDefault();
    finish();
  }

  function onVisibilityChange() {
    if (document.visibilityState === 'hidden') finish();
  }

  document.addEventListener('touchstart', onStart, { capture: true, passive: true });
  document.addEventListener('touchmove', onMove, { capture: true, passive: false });
  document.addEventListener('touchend', onEnd, { capture: true, passive: false });
  document.addEventListener('touchcancel', finish, true);
  document.addEventListener('visibilitychange', onVisibilityChange);
  view.addEventListener('blur', finish);
  let released = false;
  return () => {
    if (released) return;
    released = true;
    finish();
    document.removeEventListener('touchstart', onStart, true);
    document.removeEventListener('touchmove', onMove, true);
    document.removeEventListener('touchend', onEnd, true);
    document.removeEventListener('touchcancel', finish, true);
    document.removeEventListener('visibilitychange', onVisibilityChange);
    view.removeEventListener('blur', finish);
  };
}
