import type { Component } from 'vue';
import type { vue } from './pluginPageLibraries';
import type { PluginUILoader } from './pluginPageComponents';

export type PluginPageScope = {
  signal: AbortSignal;
  onDispose: (cleanup: () => void) => () => void;
  vue: typeof vue;
  loadUI: PluginUILoader;
};
export type PluginCreate<C = PluginPageScope> = (context: C, root: HTMLElement) => Component | Promise<Component>;
export type PluginView<C = PluginPageScope> = {
  styles?: readonly (string | URL)[];
  create: PluginCreate<C>;
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
  scroll: PluginPageScroll;
};
export type PluginPageModule = { default: PluginView<PluginPageHost> };
