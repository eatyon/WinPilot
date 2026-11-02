import { readStylesheet, readFeatureStyles } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import * as vue from 'vue';
import * as delayedLoading from '../../src/ui/useDelayedLoading.ts';
import { compileScript, compileTemplate, parse } from '@vue/compiler-sfc';
import ts from 'typescript';

const read = path => path.endsWith('.css') ? readStylesheet(new URL(`../../src/${path}`, import.meta.url)) : readFileSync(new URL(`../../src/${path}`, import.meta.url), 'utf8');

test('Cron rule configuration uses library collapse with its spacing inside', () => {
  const source = read('features/tasks/CronEditor.vue');
  for (const mode of ['range', 'interval', 'values']) {
    assert.ok(source.includes(`<div data-cron-mode="${mode}">`));
    assert.ok(source.includes(`<NCollapseTransition :show="currentRule.mode === '${mode}'" :inert="currentRule.mode !== '${mode}' || undefined">`));
    assert.ok(!source.includes(`v-if="currentRule.mode === '${mode}'"`));
  }
  assert.match(source, /\.cron-rule-dependent\s*\{\s*padding-top: 16px;/);
  const { descriptor } = parse(source);
  assert.deepEqual(compileTemplate({ source: descriptor.template.content, filename: 'CronEditor.vue', id: 'cron-transition' }).errors, []);
});

test('process trigger paths collapse with their spacing without hiding the event selector', () => {
  const source = read('features/tasks/TasksView.vue');
  const css = readFeatureStyles();
  assert.match(source, /<NCollapseTransition :show="Boolean\(editor.trigger.event\)" :inert="!editor.trigger.event \|\| undefined">\s*<NFormItem\s+:show-label="false"[\s\S]*?class="form-field task-trigger-program-reveal"/);
  assert.match(css, /\.task-trigger-program-reveal\s*\{\s*padding-top: 12px;/);
  assert.doesNotMatch(css, /\.task-trigger-process\s*\{[^}]*gap:/);
});

test('load errors delegate visibility to the library without unmounting their transition', () => {
  const source = read('components/AppLoadError.vue');
  assert.match(source, /withDefaults\(defineProps<[\s\S]*\{ show: true \}/);
  assert.match(source, /<NCollapseTransition :show="show" :inert="!show \|\| undefined"/);
  assert.doesNotMatch(source, /hasShown|initialLoad|\bappear=/);
  for (const path of [
    'home/HomeView.vue', 'desktop/DesktopView.vue', 'programs/ProgramsView.vue',
    'files/FilesView.vue', 'logs/LogsView.vue', 'tasks/TasksView.vue', 'tasks/TaskVariableHelp.vue',
    'plugins/PluginsView.vue', 'plugins/PluginDetailView.vue', 'plugins/PluginInfoView.vue',
    'plugins/PluginPageView.vue', 'settings/SettingsView.vue', 'settings/AvailableCommandsModal.vue',
    'settings/tabs/GeneralSettings.vue', 'settings/tabs/AdvancedSettings.vue', 'settings/tabs/NotificationSettings.vue', 'settings/tabs/SecuritySettings.vue',
  ]) {
    for (const tag of read(`features/${path}`).matchAll(/<AppLoadError\b[^>]*>/g)) {
      assert.match(tag[0], /:show=/, path);
      assert.doesNotMatch(tag[0], /v-(?:else-)?if=/, path);
      assert.doesNotMatch(tag[0], /initial-load=/, path);
    }
  }
});

test('load error transitions stay mounted across initial visibility, updates, recovery and later failures', async () => {
  const { descriptor } = parse(read('components/AppLoadError.vue'));
  const script = compileScript(descriptor, { id: 'load-error-lifecycle', inlineTemplate: true });
  const code = ts.transpileModule(script.content, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
  for (const initial of [false, true]) {
    const transitions = [];
    const passThrough = vue.defineComponent({ setup: (_, { slots }) => () => vue.h('div', slots.default?.()) });
    const collapse = vue.defineComponent({
      inheritAttrs: false,
      props: { show: Boolean, appear: Boolean },
      setup(props, { slots }) {
        transitions.push(props);
        return () => props.show ? vue.h('section', slots.default?.()) : null;
      },
    });
    const context = { exports: {}, require(name) {
      if (name === 'vue') return vue;
      if (name === 'naive-ui') return { NCollapseTransition: collapse, NAlert: passThrough, NFlex: passThrough, NText: passThrough };
      if (name.endsWith('/useDelayedLoading')) return delayedLoading;
      if (name.endsWith('.vue')) return { default: passThrough };
      throw new Error('Unexpected import: ' + name);
    } };
    runInNewContext(code, context);
    const node = (text = '') => ({ children: [], parent: null, text });
    const remove = child => {
      if (child.parent) child.parent.children.splice(child.parent.children.indexOf(child), 1);
      child.parent = null;
    };
    const renderer = vue.createRenderer({
      createElement: () => node(), createText: node, createComment: () => node(),
      setText: (target, text) => { target.text = text; },
      setElementText: (target, text) => { target.text = text; }, patchProp() {},
      insert(child, parent, anchor) {
        remove(child);
        parent.children.splice(anchor ? parent.children.indexOf(anchor) : parent.children.length, 0, child);
        child.parent = parent;
      },
      remove, parentNode: target => target.parent,
      nextSibling: target => target.parent?.children[target.parent.children.indexOf(target) + 1] ?? null,
    });
    const visible = vue.ref(initial), message = vue.ref('first error');
    const root = node();
    const text = target => target.text + target.children.map(text).join('');
    const app = renderer.createApp({ setup: () => () => vue.h(context.exports.default, { show: visible.value }, () => message.value) });
    app.mount(root);
    try {
      assert.equal(transitions.length, 1);
      assert.equal(transitions[0].show, initial);
      assert.equal(transitions[0].appear, false);
      visible.value = true;
      await vue.nextTick();
      assert.match(text(root), /first error/);
      message.value = 'updated error';
      await vue.nextTick();
      assert.match(text(root), /updated error/);
      visible.value = false;
      await vue.nextTick();
      assert.equal(transitions[0].show, false);
      visible.value = true;
      await vue.nextTick();
      assert.equal(transitions[0].show, true);
      assert.equal(transitions.length, 1);
    } finally { app.unmount(); }
  }
});

test('expandable content and operation results reuse library collapse transitions', () => {
  const cases = [
    ['features/files/FilesView.vue', 'rootPickerOpen'],
    ['features/programs/ProgramsView.vue', 'isExpanded(group.id)'],
    ['components/AppearanceSettingsDrawer.vue', "themeMode !== 'system'"],
    ['components/AppearanceSettingsDrawer.vue', 'pageTransitionEnabled'],
    ['features/settings/tabs/AboutSettings.vue', 'Boolean(updateMessage)'],
    ['features/terminal/TerminalView.vue', 'isSessionLimitReached'],
    ['features/plugins/PluginsView.vue', 'Boolean(plugin.errors.length)'],
    ['features/tasks/TasksView.vue', 'Boolean(unassignedTriggerError)'],
    ['features/tasks/TaskActionEditor.vue', 'Boolean(unassignedError)'],
  ];
  for (const [path, condition] of cases) {
    const source = read(path);
    assert.ok(source.includes(`<NCollapseTransition :show="${condition}"`), path);
    const { descriptor } = parse(source);
    assert.deepEqual(compileTemplate({ source: descriptor.template.content, filename: path, id: 'content-transition' }).errors, []);
  }
  const appearance = read('components/AppearanceSettingsDrawer.vue');
  assert.match(appearance, /\.appearance-settings-dependent \{ padding-top: 16px; \}/);
  assert.doesNotMatch(appearance, /\.appearance-settings-fields\s*\{[^}]*gap:/);
});

test('task-level validation keeps field gaps outside the transition and alert spacing inside it', () => {
  for (const path of ['features/tasks/TasksView.vue', 'features/tasks/TaskActionEditor.vue']) {
    const source = read(path);
    assert.match(source, /<NCollapseTransition :show="Boolean\(unassigned(?:Trigger)?Error\)">\s*<div class="task-validation-feedback">\s*<NAlert type="error" role="alert">/);
    assert.doesNotMatch(source, /<NAlert v-if="unassigned(?:Trigger)?Error"/);
  }
  const styles = readFeatureStyles();
  assert.match(styles, /\.task-action-fields, \.task-trigger-fields \{ display: grid; gap: 12px; min-width: 0; \}/);
  assert.match(styles, /\.task-validation-feedback \{ padding-top: 12px; \}/);
});

test('task continuation option collapses its spacing without changing action values', () => {
  const source = read('features/tasks/TasksView.vue');
  assert.match(source, /<TaskActionEditor[\s\S]*?\/>\s*<NCollapseTransition\s+:show="index < editor.actions.length - 1"\s+:inert="index >= editor.actions.length - 1 \|\| undefined"\s*>\s*<div class="task-action-continue">\s*<NCheckbox v-model:checked="action.continueOnFailure">/);
  assert.match(readFeatureStyles(), /\.task-action-continue\s*\{\s*padding-top: 12px;/);
  assert.doesNotMatch(source, /<NCheckbox\s+v-if="index < editor.actions.length - 1"/);
});

test('process path rows reuse action transitions and animate their own spacing', () => {
  const source = read('features/tasks/TasksView.vue');
  assert.match(source, /<TransitionGroup name="task-action" @before-leave="\(element\) => element.setAttribute\('inert', ''\)">\s*<div\s+v-for="\(_, index\) in editor.trigger.processPaths"\s+:key="processPathKeys\(editor.trigger.processPaths\)\[index\]"/);
  assert.match(source, /class="task-action-content">\s*<div class="task-trigger-path-spacing">/);
  assert.match(readFeatureStyles(), /\.task-trigger-path-spacing\s*\{\s*padding-bottom: 10px;/);
  assert.match(readFeatureStyles(), /\.form-field\.n-form-item\s*>\s*\.form-field-content\.task-trigger-programs\s*\{[^}]*gap: 0;/);
});

test('task action reordering uses Vue list movement with reduced motion support', () => {
  const css = readFeatureStyles();
  assert.match(css, /\.task-action-move\s*\{\s*transition: transform \.2s/);
  assert.match(css, /@media \(prefers-reduced-motion: reduce\)\s*\{[^{}]*\.task-action-move[^{}]*\{[^{}]*transition: none/);
  const source = read('features/tasks/TasksView.vue');
  assert.match(source, /<TransitionGroup name="task-action">\s*<div\s+v-for="\(action, index\) in editor.actions"\s+:key="actionKey\(action\)"/);
  assert.match(source, /@click="move\(editor.actions, index, -1\)"/);
  assert.match(source, /@click="move\(editor.actions, index, 1\)"/);
});

test('update feedback collapses its spacing and prevents focus on the hidden release link', () => {
  const source = read('features/settings/tabs/AboutSettings.vue');
  assert.match(source, /<NCollapseTransition :show="Boolean\(updateMessage\)" :inert="!updateMessage \|\| undefined">\s*<div class="about-update-feedback">/);
  assert.doesNotMatch(source, /<NText[^>]*v-if="updateMessage"/);
  const styles = readFeatureStyles();
  assert.match(styles, /\.about-update-feedback \{ padding-top: 14px; \}/);
  assert.match(styles, /\.about-update-status \{ margin: 0; \}/);
});

test('terminal limits and plugin errors collapse their own spacing without an initial entrance', () => {
  const terminal = read('features/terminal/TerminalView.vue');
  assert.match(terminal, /<div class="terminal-controls">\s*<NCard[^>]*content-class="terminal-toolbar"/);
  assert.match(terminal, /<NCollapseTransition :show="isSessionLimitReached" :appear="false">\s*<div class="terminal-limit-feedback">/);
  assert.match(terminal, /\.terminal-controls \{ min-width: 0; flex-shrink: 0; \}/);
  assert.match(terminal, /\.terminal-limit-feedback \{ padding-top: 12px; \}/);
  const plugins = read('features/plugins/PluginsView.vue');
  assert.match(plugins, /<NCollapseTransition :show="Boolean\(plugin.errors.length\)" :appear="false" class="plugin-card-error">\s*<div class="plugin-error-feedback">/);
  assert.match(readFeatureStyles(), /\.plugin-error-feedback \{ padding-top: 12px; \}/);
});

test('failure spacing collapses with the alert while preserving desktop grid and mobile card gaps', () => {
  const component = read('components/AppLoadError.vue');
  assert.match(component, /<div class="load-error-content">\s*<NAlert/);
  assert.match(component, /\.load-error-content \{ padding-bottom: 18px; \}/);
  assert.match(component, /\.app-load-error:not\(:last-child\) \{ margin-bottom: calc\(-1 \* var\(--load-error-layout-gap, 0px\)\); \}/);
  const styles = readFeatureStyles();
  assert.match(read('styles/components.css'), /\.load-error-layout \{ --load-error-layout-gap: var\(--page-card-row-gap\); \}/);
  assert.match(read('styles/layout.css'), /--page-card-row-gap: clamp\(16px, 3vw, 18px\)/);
  assert.doesNotMatch(styles, /\.app-load-error \+ \*/);
  assert.doesNotMatch(styles, /\.plugin-home-view\.load-error-layout\s*\{/);
  for (const path of [
    'home/HomeView.vue', 'desktop/DesktopView.vue', 'programs/ProgramsView.vue',
    'tasks/TasksView.vue', 'files/FilesView.vue', 'logs/LogsView.vue',
    'plugins/PluginsView.vue', 'plugins/PluginInfoView.vue', 'plugins/PluginDetailView.vue',
    'settings/SettingsView.vue', 'settings/tabs/GeneralSettings.vue',
    'settings/tabs/AdvancedSettings.vue', 'settings/tabs/NotificationSettings.vue', 'settings/tabs/SecuritySettings.vue',
  ]) assert.match(read(`features/${path}`), /class="[^"]*load-error-layout/, path);
  assert.match(read('features/settings/AvailableCommandsModal.vue'), /--load-error-layout-gap: 20px;/);
});
