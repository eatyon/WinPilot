import { readStylesheet, readFeatureStyles } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, readdirSync } from 'node:fs';
import postcss from 'postcss';
import { runInNewContext } from 'node:vm';
import { webcrypto } from 'node:crypto';
import { computed, reactive, ref } from 'vue';
import { loadScriptModule } from '../support/scriptModule.mjs';
import { elements, attribute, binding } from '../support/template.mjs';
import { modalStateModule } from '../support/modalState.mjs';

test('host status messages delegate their visual states to library alerts', () => {
  let count = 0;
  function inspect(directory) {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        if (entry.name !== 'page') inspect(new URL(`${entry.name}/`, directory));
        continue;
      }
      if (!entry.name.endsWith('.vue')) continue;
      const source = readFileSync(new URL(entry.name, directory), 'utf8');
      for (const node of elements(source, () => true)) {
        const classes = attribute(node, 'class') ?? '';
        if (/\b(status-message|connection-notice)\b/.test(classes)) {
          const alerts = [];
          function collectAlerts(current) {
            if (current.tag === 'NAlert') alerts.push(current);
            for (const child of current.children ?? []) collectAlerts(child);
          }
          collectAlerts(node);
          assert.equal(alerts.length, 1, entry.name);
          const type = attribute(alerts[0], 'type');
          assert.equal(type, /\berror\b|connection-notice/.test(classes) ? 'error' : /\bsuccess\b/.test(classes) ? 'success' : 'info');
          count++;
        }
      }
    }
  }
  inspect(new URL('../../src/', import.meta.url));
  assert.ok(count > 0);
  const css = postcss.parse(readFeatureStyles());
  css.walkRules(rule => {
    if (!/^\.(status-message|connection-notice)(?:\.|$)/.test(rule.selector)) return;
    rule.walkDecls(declaration => assert.ok(!/^(background|border|color|font)/.test(declaration.prop), declaration.prop));
  });
});

test('remaining cards status indicators and empty states use library surfaces', () => {
  const read = path => path.endsWith('.css') ? readStylesheet(new URL(`../../src/${path}`, import.meta.url)) : readFileSync(new URL(`../../src/${path}`, import.meta.url), 'utf8');
  const commands = read('features/settings/AvailableCommandsModal.vue');
  assert.match(commands, /<NCard v-for="group in groups"/);
  assert.match(commands, /<NList\b/);
  assert.match(commands, /<NThing/);
  assert.doesNotMatch(commands, /command-group|command-item|border-radius:|box-shadow:/);
  const bot = read('features/settings/CommandBotSettingsModal.vue');
  assert.match(bot, /<NBadge\s+dot/);
  assert.match(bot, /:feedback="prefixError"/);
  const prefix = elements(bot, node => node.tag === 'NFormItem' && attribute(node, 'label') === '命令前缀')[0];
  assert.equal(binding(prefix, 'feedback'), 'prefixError');
  const websocket = elements(bot, node => node.tag === 'NFormItem' && attribute(node, 'label') === 'WebSocket 地址')[0];
  assert.notEqual(binding(websocket, 'feedback'), 'prefixError');
  assert.doesNotMatch(bot, /command-bot-status-dot|background:/);
  const notifications = read('features/settings/tabs/NotificationSettings.vue');
  assert.match(notifications, /<AppIcon name="bot" :color="botEnabled\[channel.key\] \? 'var\(--accent-text\)' : undefined"/);
  assert.doesNotMatch(notifications, /:type="botEnabled\[channel.key\]|:secondary="Boolean\(botEnabled\[channel.key\]\)"/);
  assert.doesNotMatch(notifications, /command-bot-button.enabled/);
  const files = read('features/files/FilesView.vue');
  assert.match(files, /<NText v-else-if="item.status === 'success'" type="success">/);
  assert.doesNotMatch(files, /status-success|status-error/);
  const settings = read('features/settings/SettingsView.vue');
  const tabs = elements(settings, node => node.tag === 'NTabs' && binding(node, 'value') === 'activeKey')[0];
  assert.ok(tabs);
  assert.equal(attribute(tabs, 'type'), 'segment');
  assert.match(read('features/settings/SettingsView.vue'), /<NButton text[^>]*:aria-current=/);
  const terminal = read('features/terminal/TerminalView.vue');
  assert.match(terminal, /aria-keyshortcuts="Delete"\s+@keydown.delete.prevent="closeTab\(tab.id\)"/);
  assert.match(read('features/settings/tabs/GeneralSettings.vue'), /<NAlert type="warning" class="settings-security-notice" :show-icon="false">\s*<div class="settings-security-notice-content">\s*<AppIcon name="alert-circle"/);
  const styles = readFeatureStyles();
  assert.match(styles, /\.settings-security-notice-content \{ display: flex; align-items: center;/);
  assert.doesNotMatch(styles, /status-success|status-error/);
  assert.match(styles, /\.settings-security-notice \{ margin-top: 9px; \}/);
  for (const path of ['features/tasks/TaskVariableHelp.vue', 'features/settings/tabs/AboutSettings.vue', 'features/plugins/PluginInfoView.vue']) {
    assert.doesNotMatch(read(path), /<p[^>]*>暂无|<p[^>]*>此版本暂无/);
    assert.match(read(path), /<NEmpty/);
  }
  const cron = read('features/tasks/CronEditor.vue');
  assert.match(cron, /<NCard embedded size="small" content-class="cron-preview"/);
  assert.match(cron, /<NButton text :type="activeField === index \? 'primary' : 'default'">/);
  assert.doesNotMatch(cron, /\.cron-preview\s*\{[^}]*background:/);
});

test('program path validation is inline and cannot trigger a native browser validation bubble', () => {
  const program = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  const [form] = elements(program, node => node.tag === 'form' && attribute(node, 'class') === 'launch-form');
  assert.ok(form?.props.some(prop => prop.type === 6 && prop.name === 'novalidate'));
  assert.match(program, /:feedback="launchErrorField === 'programPath' \? launchFormError : undefined"/);
  assert.doesNotMatch(program, /<NAlert v-if="launchFormError && !launchErrorField"/);
  assert.doesNotMatch(program, /required: true/);
});

test('file path navigation uses keyboard-accessible library breadcrumbs', () => {
  const source = readFileSync(new URL('../../src/features/files/FilesView.vue', import.meta.url), 'utf8');
  assert.match(source, /<NBreadcrumb class="files-breadcrumb" aria-label="当前路径">/);
  assert.match(source, /<NBreadcrumbItem[\s\S]*?:href="index < breadcrumbs.length - 1 \? '#' : undefined"/);
  assert.match(source, /@click.prevent="index < breadcrumbs.length - 1 && browse\(crumb.path\)"/);
  assert.match(source, /<NScrollbar v-if="directory" x-scrollable class="files-breadcrumb-scrollbar">/);
  assert.doesNotMatch(source, /files-breadcrumb-link|files-breadcrumb-separator|files-breadcrumb-current/);
});

test('host forms use library labels and feedback with explicit control associations', () => {
  let count = 0;
  let customBodies = 0;
  for (const file of ['auth/LoginView', 'files/FilesView', 'logs/LogsView', 'programs/ProgramsView', 'terminal/TerminalView', 'plugins/PluginInfoView', 'plugins/PluginSettingField', 'settings/CommandBotSettingsModal', 'settings/tabs/AboutSettings', 'settings/tabs/GeneralSettings', 'settings/tabs/NotificationSettings', 'settings/tabs/SecuritySettings', 'tasks/TasksView', 'tasks/TaskActionEditor', 'tasks/CronEditor']) {
    const source = readFileSync(new URL(`../../src/features/${file}.vue`, import.meta.url), 'utf8');
    const fields = elements(source, node => node.tag === 'NFormItem');
    assert.ok(fields.length, file);
    for (const field of fields) {
      assert.notEqual(binding(field, 'show-feedback'), undefined, file);
      assert.ok(binding(field, 'show-label') === 'false' || binding(field, 'label-props'), `${file}: missing label association`);
      if (attribute(field, 'content-class')) {
        assert.equal(attribute(field, 'content-class').includes('form-field-content'), true, file);
        customBodies++;
      }
      count++;
    }
    assert.doesNotMatch(source, /class="field-error"/);
  }
  assert.ok(count >= 80);
  assert.equal(customBodies, 2);
});

test('numeric fields share the library input number instead of native number inputs', () => {
  const number = readFileSync(new URL('../../src/components/AppNumberInput.vue', import.meta.url), 'utf8');
  assert.match(number, /<NInputNumber/);
  assert.match(number, /<NInputNumber\s+style="width: 100%"/);
  assert.doesNotMatch(number, /parseFloat/);
  assert.match(number, /!Number.isFinite\(Number\(props.modelValue\)\)/);
  for (const file of ['../components/RefreshToolbar.vue', 'tasks/TasksView.vue', 'plugins/PluginSettingField.vue', 'settings/tabs/GeneralSettings.vue']) {
    const source = readFileSync(new URL(`../../src/features/${file}`, import.meta.url), 'utf8');
    assert.match(source, /<AppNumberInput/);
    assert.doesNotMatch(source, /type:\s*['"]number['"]|type="number"/);
  }
});

test('keyboard states and dynamic collection disclosure delegate to library components', () => {
  const keyboard = readFileSync(new URL('../../src/components/KeyboardKeyPicker.vue', import.meta.url), 'utf8');
  assert.equal(elements(keyboard, node => node.tag === 'NButton').length, 6);
  assert.equal([...keyboard.matchAll(/:secondary="selected.includes\(key\)"/g)].length, 4);
  assert.doesNotMatch(keyboard, /AppPicker\.css/);
  const settings = readFileSync(new URL('../../src/features/plugins/PluginSettingField.vue', import.meta.url), 'utf8');
  assert.match(settings, /<NCollapse v-model:expanded-names="expandedNames"/);
  assert.match(settings, /<template #header="\{ collapsed \}">\s*<NButton text strong class="plugin-collection-title" :aria-expanded="!collapsed">/);
  assert.doesNotMatch(settings, /#header-extra/);
  assert.match(settings, /:field-id="`collection-\$\{fieldName\(\)\}-\$\{index\}-\$\{child.name\}`"\s*\/>\s*<NFlex justify="end">\s*<NButton type="error" @click="requestDelete\(Number\(index\)\)">删除<\/NButton>\s*<\/NFlex>\s*<\/div>/);
  assert.doesNotMatch(settings, /toggleCollection|collectionCollapsed|plugin-collection-item-heading/);
  const css = readFeatureStyles();
  assert.doesNotMatch(css, /(?:^|\n)\.n-button__content\s*\{/);
  assert.match(css, /\.plugin-collection-title\.n-button\s*\{[^}]*justify-content: flex-start;[^}]*white-space: normal;[^}]*line-height: 1\.5;/);
  const styles = readFeatureStyles();
  assert.doesNotMatch(styles, /virtual-key:disabled|\.virtual-key\.selected|\.keyboard-message/);
});

test('dynamic configuration expansion follows item identities across adding and deleting', () => {
  const props = reactive({
    field: { name: 'rules', type: 'collection', mode: 'dynamic', item: { fields: [{ name: 'enabled', type: 'checkbox' }] } },
    modelValue: [{ __winpilot: { id: 'existing', remark: '已有配置' }, enabled: true }],
  });
  const code = loadScriptModule(new URL('../../src/features/plugins/PluginSettingField.vue', import.meta.url), 'expandedNames,addCollectionItem,requestDelete,confirmDelete');
  const context = {
    exports: {}, crypto: webcrypto,
    defineProps: () => props,
    defineEmits: () => (_, value) => { props.modelValue = value; },
    require: name => {
      if (name === 'vue') return { computed, ref, onBeforeUnmount() {} };
      if (name.endsWith('/pluginRequests')) return { createPluginRequests: () => ({ dispose() {} }) };
      if (name.endsWith('/useModalState')) return modalStateModule;
      return {};
    },
  };
  runInNewContext(code, context);
  const state = context.exports;
  assert.equal(state.expandedNames.value.length, 0);
  state.addCollectionItem();
  const addedId = props.modelValue[1].__winpilot.id;
  assert.deepEqual([...state.expandedNames.value], [addedId]);
  assert.equal(props.modelValue[1].enabled, false);
  state.expandedNames.value.push('existing');
  state.requestDelete(0);
  state.confirmDelete();
  assert.equal(props.modelValue.length, 1);
  assert.equal(props.modelValue[0].__winpilot.id, addedId);
  assert.deepEqual([...state.expandedNames.value], [addedId]);
  state.requestDelete(0);
  state.confirmDelete();
  assert.equal(props.modelValue.length, 0);
  assert.equal(state.expandedNames.value.length, 0);
});

test('log severity and keyboard-accessible detail expansion delegate to library components', () => {
  const source = readFileSync(new URL('../../src/features/logs/LogsView.vue', import.meta.url), 'utf8');
  assert.ok(elements(source, node => node.tag === 'NTag' && attribute(node, 'class') === 'log-level').length);
  assert.match(source, /<NCollapseItem name="details">\s*<template #header="\{ collapsed \}">\s*<NButton text attr-type="button" :aria-expanded="!collapsed">查看异常详情/);
  assert.doesNotMatch(source, /<details|<summary|\.log-level-(?:err|ftl|wrn)/);
});
