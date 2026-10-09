import type { PluginConfirmationOptions, PluginModalHandle, PluginModalOptions } from './PluginPageTypes.ts';

type ModalState = { id: number; controller: AbortController; pending: boolean };
export type PluginModalEntry = ModalState & (
  | { kind: 'content'; options: PluginModalOptions; resolve: () => void }
  | { kind: 'confirmation'; options: PluginConfirmationOptions; resolve: (accepted: boolean) => void }
);

export function createPluginModalStack(changed: () => void, reportError: (error: unknown) => void) {
  const entries: PluginModalEntry[] = [];
  let sequence = 0;
  let disposed = false;

  function close(entry: PluginModalEntry, accepted = false) {
    const index = entries.indexOf(entry);
    if (index < 0) return;
    const removed = entries.splice(index);
    changed();
    for (const item of removed.reverse()) {
      item.controller.abort();
      if (item.kind === 'content') {
        item.resolve();
      } else {
        item.resolve(item === entry && accepted);
      }
    }
  }

  function openModal(options: PluginModalOptions): PluginModalHandle {
    if (disposed) throw new DOMException('插件页面已关闭', 'AbortError');
    if (typeof options.mount !== 'function') throw new Error('插件弹窗必须声明 mount 函数');
    let resolve!: () => void;
    const closed = new Promise<void>(done => { resolve = done; });
    const entry: PluginModalEntry = {
      id: ++sequence, kind: 'content', options: { ...options, actions: options.actions?.slice() },
      controller: new AbortController(), pending: false, resolve,
    };
    entries.push(entry);
    changed();
    return {
      close: () => close(entry),
      update: changes => {
        if (!entries.includes(entry)) return;
        Object.assign(entry.options, changes);
        if (changes.actions) entry.options.actions = changes.actions.slice();
        changed();
      },
      closed,
      signal: entry.controller.signal,
    };
  }

  function confirm(options: PluginConfirmationOptions): Promise<boolean> {
    if (disposed) return Promise.resolve(false);
    return new Promise(resolve => {
      entries.push({ id: ++sequence, kind: 'confirmation', options: { ...options }, controller: new AbortController(), pending: false, resolve });
      changed();
    });
  }

  async function runAction(entry: PluginModalEntry, index: number) {
    if (entries.at(-1) !== entry || entry.kind !== 'content' || entry.pending) return;
    const action = entry.options.actions?.[index];
    if (!action || action.disabled) return;
    entry.pending = true;
    changed();
    try { await action.onClick(); }
    catch (error) { if (!entry.controller.signal.aborted) reportError(error); }
    finally { entry.pending = false; if (entries.includes(entry)) changed(); }
  }

  function dismiss(entry: PluginModalEntry) {
    if (entries.at(-1) !== entry || (entry.kind === 'content' && (entry.options.dismissible === false || entry.pending))) return;
    close(entry);
  }

  return {
    entries, openModal, confirm, close, dismiss, runAction,
    dispose() {
      if (disposed) return;
      disposed = true;
      if (entries[0]) close(entries[0]);
    },
  };
}
