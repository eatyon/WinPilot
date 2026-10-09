import type { PluginView } from './PluginPageTypes.ts';
import { createPluginPageLifetime } from './PluginPageLifetime.ts';
import { createPluginPageUi } from './PluginPageUi.ts';

export function validatePluginView(value: unknown): asserts value is PluginView {
  if (!value || typeof value !== 'object' || !('mount' in value) || typeof value.mount !== 'function') {
    throw new Error('插件页面必须声明 mount 函数');
  }
  const styles = 'styles' in value ? value.styles : undefined;
  if (styles !== undefined && (!Array.isArray(styles) || styles.some(style => !(typeof style === 'string' && style.trim()) && !(style instanceof URL)))) {
    throw new Error('插件样式必须声明为资源地址数组');
  }
}

function loadStyle(root: ShadowRoot, url: string | URL, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const link = root.ownerDocument.createElement('link');
    link.rel = 'stylesheet';
    link.href = String(url);
    const timeout = setTimeout(() => { cleanup(); reject(new Error('插件样式加载超时')); }, 15_000);
    const cleanup = () => {
      clearTimeout(timeout);
      link.removeEventListener('load', loaded);
      link.removeEventListener('error', failed);
      signal.removeEventListener('abort', aborted);
    };
    const loaded = () => { cleanup(); resolve(); };
    const failed = () => { cleanup(); reject(new Error('插件样式加载失败')); };
    const aborted = () => { cleanup(); reject(new DOMException('插件视图已关闭', 'AbortError')); };
    link.addEventListener('load', loaded, { once: true });
    link.addEventListener('error', failed, { once: true });
    signal.addEventListener('abort', aborted, { once: true });
    if (signal.aborted) { aborted(); return; }
    root.append(link);
  });
}

export function mountPluginView(
  root: ShadowRoot,
  view: PluginView,
  parent: AbortSignal,
  reportError: (error: unknown) => void,
  uiStyles = '',
) {
  const lifetime = createPluginPageLifetime(reportError);
  const dispose = () => lifetime.dispose();
  parent.addEventListener('abort', dispose, { once: true });
  lifetime.onDispose(() => {
    parent.removeEventListener('abort', dispose);
    root.replaceChildren();
  });
  if (parent.aborted) dispose();
  const ready = (async () => {
    try {
      validatePluginView(view);
      if (lifetime.signal.aborted) return;
      const base = root.ownerDocument.createElement('style');
      base.textContent = ':host { display: block; min-width: 0; color: var(--text); font-family: inherit; } button, a, label, summary, input[type="checkbox"], input[type="radio"], [role="button"], [role="option"], [role="menuitem"], [role="switch"] { -webkit-tap-highlight-color: transparent; }';
      root.append(base);
      base.textContent += uiStyles;
      await Promise.all([...new Set(view.styles?.map(String) ?? [])].map(url => loadStyle(root, url, lifetime.signal)));
      if (lifetime.signal.aborted) return;
      const cleanup = await view.mount(root, { ...lifetime, ui: createPluginPageUi(lifetime) });
      if (typeof cleanup === 'function') lifetime.onDispose(cleanup);
      if (lifetime.signal.aborted) root.replaceChildren();
    } catch (error) {
      dispose();
      throw error;
    }
  })();
  return { ready, dispose };
}
