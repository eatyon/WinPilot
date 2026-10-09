import { enhancePluginSelect } from './PluginPageSelect.ts';
import { enhancePluginMenu, type PluginMenuOptions } from './PluginPageMenu.ts';
import type { PluginPageScope } from './PluginPageTypes';

export function createPluginPageUi(scope: Pick<PluginPageScope, 'signal' | 'onDispose'>) {
  const selects = new WeakMap<ShadowRoot | HTMLElement, ReturnType<typeof enhancePluginSelect>>();
  return {
    enhanceMenu: (trigger: HTMLButtonElement, content: HTMLElement, options?: PluginMenuOptions) => enhancePluginMenu(trigger, content, scope, options),
    enhanceSelect(root: ShadowRoot | HTMLElement) {
      let select = selects.get(root);
      if (!select) {
        select = enhancePluginSelect(root, scope);
        selects.set(root, select);
      }
      return select;
    },
  };
}
