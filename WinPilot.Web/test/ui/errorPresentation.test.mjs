import { readFeatureStyles } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { compileTemplate, parse } from '@vue/compiler-sfc';

const read = path => readFileSync(new URL(`../../src/features/${path}`, import.meta.url), 'utf8');

test('load failures share centered library content and a separate retry action', () => {
  const source = readFileSync(new URL('../../src/components/AppLoadError.vue', import.meta.url), 'utf8');
  assert.match(source, /<NAlert[^>]*:show-icon="false"/);
  assert.match(source, /<NFlex align="center" :wrap="false"/);
  assert.match(source, /size="small"\s+text/);
  assert.match(source, /overflow-wrap: anywhere/);
  assert.match(source, /<AppTooltip v-if="retry" text="重试">/);
  assert.match(source, /aria-label="重试"/);
  assert.match(source, /<template #icon><AppIcon name="rotate-cw"/);
  assert.match(source, /@click="retryLoad"/);
  assert.match(source, /await props.retry\(\)/);
  assert.match(source, /finally \{\s*retrying.value = false/);
  assert.doesNotMatch(source, /\.n-alert__/);
  const { descriptor } = parse(source);
  assert.deepEqual(compileTemplate({ source: descriptor.template.content, filename: 'AppLoadError.vue', id: 'load-error' }).errors, []);
  for (const path of ['home/HomeView.vue', 'desktop/DesktopView.vue', 'programs/ProgramsView.vue', 'tasks/TasksView.vue', 'files/FilesView.vue', 'logs/LogsView.vue', 'plugins/PluginsView.vue', 'plugins/PluginDetailView.vue', 'plugins/PluginInfoView.vue', 'plugins/PluginPageView.vue', 'settings/SettingsView.vue', 'settings/tabs/AdvancedSettings.vue', 'settings/tabs/NotificationSettings.vue', 'settings/tabs/SecuritySettings.vue']) {
    const page = read(path);
    assert.match(page, /<AppLoadError/);
    assert.doesNotMatch(page, /<NAlert[^>]*class="status-message error"[^>]*>[\s\S]*?重试[\s\S]*?<\/NAlert>/);
  }
  for (const path of ['tasks/TaskVariableHelp.vue', 'settings/AvailableCommandsModal.vue']) {
    const page = read(path);
    assert.match(page, /defineProps</);
    assert.doesNotMatch(page, /<AppLoadError|<AppLoading|apiFetch|requests\.fetch/);
  }
  assert.match(read('programs/ProgramsView.vue'), /:retry="retryPage"/);
});

test('refresh keeps loaded content and failure states in place instead of replacing them', () => {
  const tasks = read('tasks/TasksView.vue');
  assert.doesNotMatch(tasks, /v-show="tasksReadState !== 'loading'"/);
  assert.match(tasks, /tasksReadState === 'loading' && !tasksLoaded && !pageError/);
  assert.match(read('plugins/PluginsView.vue'), /loading && !hasLoaded && !error/);
  assert.doesNotMatch(read('files/FilesView.vue'), /v-show="!loading"/);
  assert.match(read('logs/LogsView.vue'), /v-else-if="entries.length"/);
  const errorComponent = readFileSync(new URL('../../src/components/AppLoadError.vue', import.meta.url), 'utf8');
  assert.match(errorComponent, /:loading="showLoading"\s+:disabled="disabled \|\| loading \|\| retrying"/);
});

test('page errors own their bottom spacing and avoid stacking an existing layout gap', () => {
  const styles = readFeatureStyles();
  for (const selector of ['tasks-view', 'settings-tab-content', 'plugin-home-view']) {
    assert.match(styles, new RegExp(`\\.${selector} \\{[^}]*gap: var\\(--page-card-row-gap\\) 18px;`));
  }
  assert.doesNotMatch(styles, /\.notification-settings-content\s*\{[^}]*gap:/);
  const component = readFileSync(new URL('../../src/components/AppLoadError.vue', import.meta.url), 'utf8');
  assert.match(component, /\.app-load-error \{ grid-column: 1 \/ -1; \}/);
  assert.match(component, /padding-bottom: 18px/);
  assert.match(component, /\.app-load-error:not\(:last-child\) \{ margin-bottom: calc\(-1 \* var\(--load-error-layout-gap, 0px\)\); \}/);
  assert.doesNotMatch(component, /margin-top:/);
});

test('loading errors stay inline while write failures have a single toast presentation', () => {
  const programs = read('programs/ProgramsView.vue');
  assert.doesNotMatch(programs, /<NAlert[^>]*class="launch-error"/);
  assert.match(programs, /showToast\(launchFormError.value\)/);
  const notifications = read('settings/tabs/NotificationSettings.vue');
  assert.match(notifications, /showToast\(userErrorMessage\(cause, '无法保存通知设置'\)\)/);
  for (const path of ['home/HomeView.vue', 'desktop/DesktopView.vue', 'programs/ProgramsView.vue', 'tasks/TasksView.vue', 'files/FilesView.vue', 'logs/LogsView.vue', 'plugins/PluginsView.vue', 'plugins/PluginDetailView.vue', 'plugins/PluginInfoView.vue', 'settings/SettingsView.vue', 'settings/tabs/GeneralSettings.vue', 'settings/tabs/SecuritySettings.vue', 'settings/tabs/AdvancedSettings.vue']) {
    const { descriptor } = parse(read(path));
    const result = compileTemplate({ source: descriptor.template.content, filename: path, id: 'error-presentation' });
    assert.deepEqual(result.errors, []);
  }
});

test('failed content reads retain structure and previously loaded lists', () => {
  const home = read('home/HomeView.vue');
  assert.doesNotMatch(home, /v-if="!errorMessage" class="system-status-grid"/);
  assert.match(home, /:show="Boolean\(errorMessage \|\| pluginError\)"/);
  assert.doesNotMatch(read('desktop/DesktopView.vue'), /desktop-frame-error|v-else-if="screenshotError"/);
  assert.match(read('tasks/TasksView.vue'), /:show="Boolean\(pageError \|\| extensionsError\)"/);
  assert.doesNotMatch(read('tasks/TasksView.vue'), /<NAlert[^>]*v-if="tasksReadState === 'error'"/);
  assert.doesNotMatch(read('files/FilesView.vue'), /v-if="directory && !pageError"/);
  assert.doesNotMatch(read('logs/LogsView.vue'), /entries.length && !isLoading && !pageError/);
  assert.match(read('plugins/PluginsView.vue'), /v-else-if="plugins.length"/);
  assert.doesNotMatch(read('plugins/PluginDetailView.vue'), /<template v-else-if="plugin && !error"/);
  const settings = read('settings/SettingsView.vue');
  assert.match(settings, /:show="Boolean\(errorMessage && activeKey === 'general' && !settingsLoaded\)"/);
  assert.doesNotMatch(settings, /<form v-if=/);
});

test('failed loads use component empty states rather than dash-only placeholders', () => {
  for (const path of ['tasks/TasksView.vue', 'plugins/PluginsView.vue', 'plugins/PluginInfoView.vue', 'plugins/PluginDetailView.vue', 'logs/LogsView.vue', 'files/FilesView.vue', 'desktop/DesktopView.vue']) {
    const source = read(path);
    assert.doesNotMatch(source, /description="—"|\? '—' :/);
    assert.match(source, /无法加载/);
  }
  const general = read('settings/tabs/GeneralSettings.vue');
  assert.doesNotMatch(general, />—</);
  assert.match(general, /:model-value="port"/);
  assert.match(general, /:model-value="startupMode"/);
  assert.doesNotMatch(general, /indeterminate|未能加载/);
  const security = read('settings/tabs/SecuritySettings.vue');
  assert.doesNotMatch(security, />—</);
  assert.match(security, /v-model="notifications.channel"/);
  assert.match(security, /:options="channelOptions"/);
  assert.doesNotMatch(security, /未能加载/);
});
