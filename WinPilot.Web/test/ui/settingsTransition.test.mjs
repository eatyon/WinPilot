import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { compileTemplate, parse } from '@vue/compiler-sfc';
import * as vue from 'vue';
import { loadScriptModule } from '../support/scriptModule.mjs';

test('settings tab direction follows route order and shares the page animation switch', () => {
  const route = vue.reactive({ query: {} });
  const enabled = vue.ref(false);
  const pushes = [];
  const context = { exports: {}, require(name) {
    if (name === 'vue') return { ...vue, onMounted() {}, onBeforeUnmount() {} };
    if (name === 'vue-router') return {
      useRoute: () => route,
      useRouter: () => ({ push: value => { pushes.push(value); route.query = value.query; } }),
    };
    if (name.endsWith('/panelEffects')) return { usePanelEffects: () => ({ pageTransitionEnabled: enabled }) };
    if (name.endsWith('/settingsRequests')) return { createSettingsRequests: () => ({ dispose() {} }) };
    if (name.endsWith('/safeMode')) return { safeModeEnabled: vue.ref(false) };
    return {};
  } };
  const scope = vue.effectScope();
  try {
    scope.run(() => runInNewContext(loadScriptModule(new URL('../../src/features/settings/SettingsView.vue', import.meta.url),
      'activeKey,tabTransition,pageTransitionEnabled,selectTab'), context));
    const subject = context.exports;
    assert.equal(subject.pageTransitionEnabled.value, false);
    enabled.value = true;
    assert.equal(subject.pageTransitionEnabled.value, true);
    subject.selectTab('about');
    assert.equal(subject.tabTransition.value, 'settings-slide-next');
    subject.selectTab('about');
    assert.equal(pushes.length, 1);
    route.query = { tab: 'notification' };
    assert.equal(subject.tabTransition.value, 'settings-slide-previous');
    route.query = { tab: 'security' };
    assert.equal(subject.tabTransition.value, 'settings-slide-next');
    enabled.value = false;
    assert.equal(subject.pageTransitionEnabled.value, false);
    route.query = { tab: 'unknown' };
    assert.equal(subject.activeKey.value, 'general');
    assert.equal(subject.tabTransition.value, 'settings-slide-previous');
  } finally { scope.stop(); }
});

test('settings uses one keyed transition without appearing on initial load or clipping into a scrollbar', () => {
  const source = readFileSync(new URL('../../src/features/settings/SettingsView.vue', import.meta.url), 'utf8');
  assert.match(source, /<Transition :name="tabTransition" :css="pageTransitionEnabled" :mode="pageTransitionEnabled \? 'out-in' : undefined">/);
  assert.match(source, /<div :key="activeKey"/);
  assert.doesNotMatch(source, /<Transition[^>]*\bappear\b|<KeepAlive/);
  assert.match(source, /overflow-x: clip/);
  assert.match(source, /prefers-reduced-motion: reduce/);
  assert.match(source, /leave-active[^{}]*\{ pointer-events: none; \}/);
  const { descriptor } = parse(source);
  assert.deepEqual(compileTemplate({ source: descriptor.template.content, filename: 'SettingsView.vue', id: 'settings-transition' }).errors, []);
});
