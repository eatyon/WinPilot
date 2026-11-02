// 插件按名称请求共享组件，每项独立动态导入，避免一次页面打开加载整套 UI。
export const pluginUiLoaders = {
  AppModal: () => import('./components/AppModal.ts').then(module => module.default),
  AppTooltip: () => import('./components/AppTooltip.ts').then(module => module.default),
  NAlert: () => import('./components/NAlert.ts').then(module => module.default),
  NBadge: () => import('./components/NBadge.ts').then(module => module.default),
  NBreadcrumb: () => import('./components/NBreadcrumb.ts').then(module => module.default),
  NBreadcrumbItem: () => import('./components/NBreadcrumbItem.ts').then(module => module.default),
  NButton: () => import('./components/NButton.ts').then(module => module.default),
  NCard: () => import('./components/NCard.ts').then(module => module.default),
  NCheckbox: () => import('./components/NCheckbox.ts').then(module => module.default),
  NCheckboxGroup: () => import('./components/NCheckboxGroup.ts').then(module => module.default),
  NCollapse: () => import('./components/NCollapse.ts').then(module => module.default),
  NCollapseItem: () => import('./components/NCollapseItem.ts').then(module => module.default),
  NCollapseTransition: () => import('./components/NCollapseTransition.ts').then(module => module.default),
  NDrawer: () => import('./components/NDrawer.ts').then(module => module.default),
  NDrawerContent: () => import('./components/NDrawerContent.ts').then(module => module.default),
  NDropdown: () => import('./components/NDropdown.ts').then(module => module.default),
  NEllipsis: () => import('./components/NEllipsis.ts').then(module => module.default),
  NEmpty: () => import('./components/NEmpty.ts').then(module => module.default),
  NFlex: () => import('./components/NFlex.ts').then(module => module.default),
  NFormItem: () => import('./components/NFormItem.ts').then(module => module.default),
  NIcon: () => import('./components/NIcon.ts').then(module => module.default),
  NImagePreview: () => import('./components/NImagePreview.ts').then(module => module.default),
  NInput: () => import('./components/NInput.ts').then(module => module.default),
  NInputGroup: () => import('./components/NInputGroup.ts').then(module => module.default),
  NInputNumber: () => import('./components/NInputNumber.ts').then(module => module.default),
  NList: () => import('./components/NList.ts').then(module => module.default),
  NListItem: () => import('./components/NListItem.ts').then(module => module.default),
  NModal: () => import('./components/NModal.ts').then(module => module.default),
  NPagination: () => import('./components/NPagination.ts').then(module => module.default),
  NPopover: () => import('./components/NPopover.ts').then(module => module.default),
  NProgress: () => import('./components/NProgress.ts').then(module => module.default),
  NRadio: () => import('./components/NRadio.ts').then(module => module.default),
  NRadioGroup: () => import('./components/NRadioGroup.ts').then(module => module.default),
  NScrollbar: () => import('./components/NScrollbar.ts').then(module => module.default),
  NSelect: () => import('./components/NSelect.ts').then(module => module.default),
  NSkeleton: () => import('./components/NSkeleton.ts').then(module => module.default),
  NSpace: () => import('./components/NSpace.ts').then(module => module.default),
  NSpin: () => import('./components/NSpin.ts').then(module => module.default),
  NSwitch: () => import('./components/NSwitch.ts').then(module => module.default),
  NTab: () => import('./components/NTab.ts').then(module => module.default),
  NTabs: () => import('./components/NTabs.ts').then(module => module.default),
  NTag: () => import('./components/NTag.ts').then(module => module.default),
  NText: () => import('./components/NText.ts').then(module => module.default),
  NThing: () => import('./components/NThing.ts').then(module => module.default),
  useLoadingBar: () => import('./components/useLoadingBar.ts').then(module => module.default),
  useDelayedLoading: () => import('./components/useDelayedLoading.ts').then(module => module.default),
  useMessage: () => import('./components/useMessage.ts').then(module => module.default),
  useThemeVars: () => import('./components/useThemeVars.ts').then(module => module.default),
  withSelectLabels: () => import('./components/withSelectLabels.ts').then(module => module.default),
} as const;

export type PluginUIName = keyof typeof pluginUiLoaders;
type PluginUI = { [Name in PluginUIName]: Awaited<ReturnType<typeof pluginUiLoaders[Name]>> };
export type PluginUILoader = <const Names extends readonly PluginUIName[]>(names: Names) => Promise<Pick<PluginUI, Names[number]>>;

export async function loadPluginUI<const Names extends readonly PluginUIName[]>(
  names: Names,
  signal?: AbortSignal,
): Promise<Pick<PluginUI, Names[number]>> {
  signal?.throwIfAborted();
  if (!Array.isArray(names)) throw new Error('共享 UI 名称必须是数组');
  for (const name of names) {
    if (typeof name !== 'string' || !Object.hasOwn(pluginUiLoaders, name)) {
      throw new Error(`不支持的共享 UI 项：${String(name)}`);
    }
  }
  const entries = await Promise.all([...new Set<PluginUIName>(names)].map(async name => {
    const component = await pluginUiLoaders[name]();
    return [name, component] as const;
  }));
  signal?.throwIfAborted();
  return Object.fromEntries(entries) as Pick<PluginUI, Names[number]>;
}
