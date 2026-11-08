import { readFileSync } from 'node:fs';
import postcss from 'postcss';
import { parse } from '@vue/compiler-sfc';

export function readStylesheet(url, ancestors = new Set()) {
  if (ancestors.has(url.href)) throw new Error(`Circular stylesheet import: ${url.href}`);
  const visited = new Set([...ancestors, url.href]);
  const raw = readFileSync(url, 'utf8');
  if (url.pathname.endsWith('.vue')) {
    return parse(raw).descriptor.styles.map(style => style.src
      ? readStylesheet(new URL(style.src, url), visited)
      : style.content).join('\n');
  }
  const sheet = postcss.parse(raw);
  sheet.walkAtRules('import', rule => {
    const relative = rule.params.match(/^['"](.+?)['"]$/)?.[1];
    if (!relative?.startsWith('.')) throw new Error(`Unsupported stylesheet import: ${rule.params}`);
    const imported = postcss.parse(readStylesheet(new URL(relative, url), visited));
    rule.replaceWith(...imported.nodes);
  });
  return sheet.toString();
}

let featureStyles;

export function readFeatureStyles() {
  if (featureStyles !== undefined) return featureStyles;
  const files = [
    'styles/features.css',
    'features/auth/LoginView.vue',
    'features/home/HomeView.vue',
    'features/desktop/DesktopView.vue',
    'features/programs/ProgramsView.vue',
    'components/KeyboardKeyPicker.vue',
    'App.vue',
    'components/AppTooltip.vue',
    'components/AppModal.vue',
    'components/AppIcon.vue',
    'features/logs/LogsView.vue',
    'features/settings/SettingsView.vue',
    'features/settings/tabs/SecuritySettings.vue',
    'features/settings/tabs/AboutSettings.vue',
    'features/settings/tabs/GeneralSettings.vue',
    'features/settings/tabs/NotificationSettings.vue',
    'features/tasks/TasksView.vue',
    'features/tasks/CronEditor.vue',
    'features/tasks/TaskActionEditor.vue',
    'features/tasks/TaskVariableHelp.vue',
    'features/plugins/PluginsView.vue',
    'features/plugins/PluginInfoView.vue',
    'features/plugins/PluginHeader.vue',
    'features/plugins/PluginDetailView.vue',
    'features/plugins/PluginSettingField.vue',
    'features/plugins/PluginMarketView.vue',
  ];
  featureStyles = files.map(file => readStylesheet(new URL(`../../src/${file}`, import.meta.url))).join('\n');
  return featureStyles;
}
