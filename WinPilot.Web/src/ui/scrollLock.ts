type ScrollLockState = {
  count: number;
  overflow: string;
  overscrollBehavior: string;
  pageLayout?: { scrollbarGutter: string; paddingRight: string };
};

// 同一滚动容器可能被多层弹窗锁定，最后一个持有者释放后才恢复原样式。
const locks = new WeakMap<HTMLElement, ScrollLockState>();

export function getPageScroller(): HTMLElement {
  return document.querySelector<HTMLElement>('[data-page-scroller] > .n-scrollbar-container')
    ?? (document.scrollingElement as HTMLElement | null)
    ?? document.documentElement;
}

export function lockScroll(...targets: Array<HTMLElement | null | undefined>): () => void {
  const elements = [...new Set(targets.filter((target): target is HTMLElement => Boolean(target)))];
  for (const element of elements) {
    const existing = locks.get(element);
    if (existing) {
      existing.count++;
      continue;
    }
    const state: ScrollLockState = {
      count: 1,
      overflow: element.style.overflow,
      overscrollBehavior: element.style.overscrollBehavior,
    };
    const view = element.ownerDocument.defaultView;
    if (element === element.ownerDocument.documentElement && view) {
      // 隐藏根滚动条时补偿原占位宽度，避免弹窗打开后背景内容横向移动。
      const gutterWidth = Math.max(0, view.innerWidth - element.getBoundingClientRect().width);
      state.pageLayout = { scrollbarGutter: element.style.scrollbarGutter, paddingRight: element.style.paddingRight };
      const paddingRight = parseFloat(view.getComputedStyle(element).paddingRight) || 0;
      element.style.scrollbarGutter = 'auto';
      element.style.paddingRight = `${paddingRight + gutterWidth}px`;
    }
    locks.set(element, state);
    element.style.overflow = 'hidden';
    element.style.overscrollBehavior = 'none';
  }

  let released = false;
  return () => {
    if (released) return;
    released = true;
    for (const element of elements) {
      const state = locks.get(element);
      if (!state || --state.count > 0) continue;
      element.style.overflow = state.overflow;
      element.style.overscrollBehavior = state.overscrollBehavior;
      if (state.pageLayout) {
        element.style.scrollbarGutter = state.pageLayout.scrollbarGutter;
        element.style.paddingRight = state.pageLayout.paddingRight;
      }
      locks.delete(element);
    }
  };
}
