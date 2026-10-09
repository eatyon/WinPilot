import type { PluginPageScroll } from './PluginPageTypes.ts';

export function createPluginPageScroll(root: ShadowRoot, signal: AbortSignal, getScroller: () => HTMLElement): PluginPageScroll {
  return {
    getPosition() {
      const scroller = getScroller();
      return { top: scroller.scrollTop, left: scroller.scrollLeft };
    },
    restorePosition(position, behavior = 'instant') {
      if (signal.aborted) return;
      if (!Number.isFinite(position.top) || !Number.isFinite(position.left)) throw new Error('滚动位置无效');
      getScroller().scrollTo({ top: Math.max(0, position.top), left: Math.max(0, position.left), behavior });
    },
    scrollTo(element, options = {}) {
      if (signal.aborted) return;
      if (!root.contains(element) || !element.isConnected) throw new Error('只能滚动到当前插件页面中的元素');
      const offset = options.offset ?? 0;
      if (!Number.isFinite(offset)) throw new Error('滚动偏移无效');
      const scroller = getScroller();
      const viewportTop = scroller === scroller.ownerDocument.scrollingElement || scroller === scroller.ownerDocument.documentElement
        ? 0 : scroller.getBoundingClientRect().top + scroller.clientTop;
      scroller.scrollTo({ top: Math.max(0, scroller.scrollTop + element.getBoundingClientRect().top - viewportTop - offset), behavior: options.behavior ?? 'instant' });
    },
  };
}
