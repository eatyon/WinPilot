import type { createPluginPageUi } from './PluginPageUi';

export type PluginPageScope = {
  signal: AbortSignal;
  onDispose: (cleanup: () => void) => () => void;
  ui: ReturnType<typeof createPluginPageUi>;
};
export type PluginMount<C = PluginPageScope> = (root: ShadowRoot, context: C) => void | (() => void) | Promise<void | (() => void)>;
export type PluginView<C = PluginPageScope> = {
  styles?: readonly (string | URL)[];
  mount: PluginMount<C>;
};
export type PluginModalSize = 'small' | 'medium' | 'large';
export type PluginModalAction = {
  label: string;
  onClick: () => void | Promise<void>;
  kind?: 'primary' | 'secondary' | 'danger';
  disabled?: boolean;
};
export type PluginModalOptions = PluginView & {
  title: string;
  size?: PluginModalSize;
  actions?: PluginModalAction[];
  dismissible?: boolean;
  dismissOnBackdrop?: boolean;
};
export type PluginModalChanges = Partial<Pick<PluginModalOptions, 'title' | 'size' | 'actions' | 'dismissible' | 'dismissOnBackdrop'>>;
export type PluginModalHandle = {
  close: () => void;
  update: (changes: PluginModalChanges) => void;
  closed: Promise<void>;
  signal: AbortSignal;
};
export type PluginConfirmationOptions = {
  message: string;
  title?: string;
  acceptText?: string;
  danger?: boolean;
};
export type PluginScrollPosition = { top: number; left: number };
export type PluginPageScroll = {
  getPosition: () => PluginScrollPosition;
  restorePosition: (position: PluginScrollPosition, behavior?: ScrollBehavior) => void;
  scrollTo: (element: HTMLElement, options?: { offset?: number; behavior?: ScrollBehavior }) => void;
};
export type PluginPageHost = PluginPageScope & {
  sessionId: string;
  assetUrl: (path: string) => string;
  send: (command: string, parameters?: Record<string, unknown>, signal?: AbortSignal) => Promise<unknown>;
  upload: (command: string, content: Blob, parameters?: Record<string, unknown>, signal?: AbortSignal) => Promise<unknown>;
  confirm: (options: PluginConfirmationOptions) => Promise<boolean>;
  openModal: (options: PluginModalOptions) => PluginModalHandle;
  notify: (message: string, type?: 'success' | 'error') => void;
  scroll: PluginPageScroll;
};
export type PluginPageModule = { default: PluginView<PluginPageHost> };
