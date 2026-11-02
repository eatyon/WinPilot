import {
  isPluginCollection,
  isPluginSetting,
  type PluginConfigurationDefinition,
  type PluginDynamicConfigurationItem,
} from './pluginModels';

export function configurationError(
  definitions: readonly PluginConfigurationDefinition[],
  config: Record<string, unknown>,
): string {
  const invalid = definitions.find(
    setting => isPluginSetting(setting) && setting.type !== 'number' && setting.required &&
      (config[setting.name] === '' || config[setting.name] === undefined),
  );
  if (invalid) return `${invalid.label || invalid.name}不能为空`;

  for (const collection of definitions.filter(isPluginCollection)) {
    if (collection.mode !== 'dynamic' || !collection.item) continue;
    const value = config[collection.name];
    if (!Array.isArray(value)) continue;
    const items = value as PluginDynamicConfigurationItem[];
    if (items.some(item => typeof item.__winpilot?.remark !== 'string' || !item.__winpilot.remark.trim()))
      return '备注不能为空';
    for (const item of items) {
      const field = collection.item.fields.find(field => {
        const value = item[field.name];
        return field.type !== 'number' && field.required &&
          (value === undefined || value === null || value === '' ||
            (Array.isArray(value) && value.length === 0));
      });
      if (field) return `${field.label || field.name}不能为空`;
    }
  }
  return '';
}
