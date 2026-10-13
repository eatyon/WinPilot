export type PluginState = 'Discovered' | 'Disabled' | 'Loaded' | 'Running' | 'Stopped' | 'Failed' | 'Invalid'

export interface PluginAuthor {
  name: string | null
  links: string | null
}

export interface PluginManifest {
  id: string
  name: string
  version: string
  authors: PluginAuthor[] | null
  description: string | null
  winpilot_version: string | null
  status_ui: boolean
  features_ui: string | null
  settings_ui: string | null
  main: string
}

export interface PluginInfo {
  id: string
  directoryName: string
  directory: string
  dataDirectory: string
  manifest: PluginManifest | null
  state: PluginState
  enabled: boolean
  errors: string[]
  hasError: boolean
  canChangeEnabled: boolean
}

export interface PluginStatusDisplaySettings {
  registerOnHome: boolean
  statusKeys: string[]
  homeTitle: string
}

export interface PluginFeatureDisplaySettings {
  registerOnHome: boolean
  featureIds: string[]
  homeTitle: string
}

export interface PluginSettingsDisplaySettings {
  registerOnHome: boolean
}

export const stateLabel: Record<PluginState, string> = {
  Discovered: '未加载',
  Disabled: '未加载',
  Loaded: '已加载',
  Running: '运行中',
  Stopped: '已停止',
  Failed: '启动失败',
  Invalid: '未加载'
}

type CollectionItem = { name: string; label?: string; fields: PluginSettingField[] };
type SelectOption = string | { value: string; label: string };
export type PluginSettingField = {
  name: string;
  type: 'text' | 'number' | 'select' | 'checkbox' | 'multi-checkbox' | 'textarea' | 'collection';
  label?: string;
  description?: string;
  default?: unknown;
  min?: number;
  max?: number;
  step?: number | 'any';
  options?: SelectOption[];
  optionsSource?: 'notificationChannels';
  required?: boolean;
  masked?: boolean;
  mode?: 'fixed' | 'dynamic';
  items?: CollectionItem[];
  item?: { fields: PluginSettingField[] };
};

export type PluginSettingDefinition = PluginSettingField & {
  type: 'text' | 'number' | 'select' | 'checkbox' | 'multi-checkbox' | 'textarea';
};
export type PluginCollectionDefinition = PluginSettingField & { type: 'collection'; mode: 'fixed' | 'dynamic' };
export type PluginFeatureDefinition = {
  id: string; name: string; description?: string; mode: 'action' | 'page'; entry?: string; confirm?: boolean;
};
export function isPluginSetting(value: unknown): value is PluginSettingDefinition {
  if (!value || typeof value !== 'object') return false;
  const field = value as Partial<PluginSettingField>;
  return typeof field.name === 'string' && field.name.length > 0 &&
    ['text', 'number', 'select', 'checkbox', 'multi-checkbox', 'textarea'].includes(field.type ?? '');
}
export function isPluginCollection(value: unknown): value is PluginCollectionDefinition {
  if (!value || typeof value !== 'object') return false;
  const field = value as Partial<PluginSettingField>;
  return field.type === 'collection' && typeof field.name === 'string' && field.name.length > 0 &&
    (field.mode === 'fixed' || field.mode === 'dynamic');
}
