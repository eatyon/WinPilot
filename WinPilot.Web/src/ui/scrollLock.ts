type ScrollLockState = {
  count: number;
  overflow: string;
  overscrollBehavior: string;
  pageLayout?: { scrollbarGutter: string; paddingRight: string };
};

const locks = new WeakMap<HTMLElement, ScrollLockState>();

export function getPageScroller(): HTMLElement {
  return (document.scrollingElement as HTMLElement | null) ?? document.documentElement;
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
