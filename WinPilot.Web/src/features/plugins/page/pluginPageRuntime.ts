import type { PluginView } from './pluginPageTypes.ts';
import { createPluginPageLifetime } from './pluginPageLifetime.ts';

export function validatePluginView(value: unknown): asserts value is PluginView {
  if (!value || typeof value !== 'object' || !('create' in value) || typeof value.create !== 'function') {
    throw new Error('插件页面必须声明 create 函数');
  }
  const styles = 'styles' in value ? value.styles : undefined;
  if (styles !== undefined && (!Array.isArray(styles) || styles.some(style => !(typeof style === 'string' && style.trim()) && !(style instanceof URL)))) {
    throw new Error('插件样式必须声明为资源地址数组');
  }
}

function loadStyle(root: HTMLElement, url: string | URL, signal: AbortSignal, styles: HTMLLinkElement[]): Promise<void> {
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
    styles.push(link);
  });
}

// 每次打开独立准备样式与组件；取消后丢弃异步结果，组件准备完成前由此处移除已插入的样式。
export function preparePluginView(
  root: HTMLElement,
  view: PluginView,
  parent: AbortSignal,
  reportError: (error: unknown) => void,
) {
  const lifetime = createPluginPageLifetime(reportError);
  const styles: HTMLLinkElement[] = [];
  let componentPrepared = false;
  const dispose = () => lifetime.dispose();
  parent.addEventListener('abort', dispose, { once: true });
  lifetime.onDispose(() => {
    parent.removeEventListener('abort', dispose);
    if (!componentPrepared) for (const style of styles) style.remove();
    styles.length = 0;
  });
  if (parent.aborted) dispose();
  const ready = (async () => {
    try {
      validatePluginView(view);
      if (lifetime.signal.aborted) return;
      // 样式与宿主库准备完成后才调用插件 create，避免组件先显示再跳变样式。
      const [libraries] = await Promise.all([
        import('./pluginPageLibraries.ts'),
        ...[...new Set(view.styles?.map(String) ?? [])].map(url => loadStyle(root, url, lifetime.signal, styles)),
      ]);
      if (lifetime.signal.aborted) return;
      const component = await view.create({
        signal: lifetime.signal,
        onDispose: lifetime.onDispose,
        vue: libraries.vue,
        loadUI: names => libraries.loadPluginUI(names, lifetime.signal),
      }, root);
      if (lifetime.signal.aborted) return;
      if (!component || !['object', 'function'].includes(typeof component) || Array.isArray(component)) {
        throw new Error('插件 create 必须返回 Vue 组件');
      }
      componentPrepared = true;
      return component;
    } catch (error) {
      dispose();
      throw error;
    }
  })();
  return { ready, dispose, signal: lifetime.signal };
}
