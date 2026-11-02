import { readStylesheet } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';
import { binding, elements, event } from '../support/template.mjs';

test('appearance settings drawer reuses existing theme options and only exposes implemented effects', () => {
  const source = readFileSync(new URL('../../src/components/AppearanceSettingsDrawer.vue', import.meta.url), 'utf8');
  assert.match(source, /:width="320"/);
  assert.doesNotMatch(source, /\bresizable\b/);
  assert.match(source, /:native-scrollbar="false"/);
  assert.match(source, /useTheme\(\)/);
  assert.match(source, /usePanelEffects\(\)/);
  assert.match(source, /v-for="\(option, index\) in accentOptions"/);
  assert.match(source, /:placement="index < 5 \? 'top' : 'bottom'"/);
  assert.doesNotMatch(source, /localStorage|fetch\(|\/api\/|RGB|背景图|色弱|NColorPicker/);
  assert.match(source, /<span>页面切换动画<\/span>/);
  const animationCard = source.match(/<NCard title="动画效果"[^>]*>([\s\S]*?)<\/NCard>/)?.[1];
  assert.ok(animationCard);
  assert.ok(source.indexOf('title="动画效果"') < source.indexOf('title="视觉效果"'));
  assert.doesNotMatch(animationCard, /loadingBarEnabled/);
  assert.match(animationCard, /aria-label="交互动画"/);
  assert.match(animationCard, /aria-label="页面切换动画"/);
  const labels = [...animationCard.matchAll(/aria-label="([^"]+)"/g)].map(match => match[1]);
  assert.deepEqual(labels, ['交互动画', '页面切换动画', '页面切换效果']);
  const visualCard = source.match(/<NCard title="视觉效果"[^>]*>([\s\S]*?)<\/NCard>/)?.[1];
  assert.ok(visualCard);
  assert.match(visualCard, /<NSwitch :value="loadingBarEnabled" aria-label="顶部进度条" @update:value="setLoadingBarEnabled"/);
  assert.deepEqual([...visualCard.matchAll(/aria-label="([^"]+)"/g)].map(match => match[1]), ['顶部进度条', '对话框背景模糊']);
  assert.match(source, /<NSwitch :value="interactionAnimationEnabled" aria-label="交互动画" @update:value="setInteractionAnimationEnabled"/);
  assert.match(source, /<NCollapseTransition :show="pageTransitionEnabled"[^>]*>\s*<div class="appearance-settings-row appearance-settings-dependent">\s*<span>页面切换效果<\/span>/);
  const effectSelect = elements(source, node => node.tag === 'AppSelect' && binding(node, 'model-value') === 'pageTransitionEffect')[0];
  assert.ok(effectSelect);
  assert.equal(binding(effectSelect, 'options'), 'pageTransitionEffectOptions');
  const effects = [];
  const updateEffect = runInNewContext(`($event) => { ${event(effectSelect, 'update:model-value')} }`, {
    setPageTransitionEffect: value => effects.push(value),
  });
  updateEffect('slide');
  updateEffect('');
  assert.deepEqual(effects, ['slide']);
  assert.doesNotMatch(source, /:disabled="!pageTransitionEnabled"/);
  const settings = readFileSync(new URL('../../src/features/settings/SettingsView.vue', import.meta.url), 'utf8');
  assert.doesNotMatch(settings, /AppearanceSettings|key: 'appearance'/);
  const app = readFileSync(new URL('../../src/App.vue', import.meta.url), 'utf8');
  assert.match(app, /aria-label="外观设置"/);
  assert.match(source, /title="外观设置"/);
  assert.match(app, /<AppIcon name="palette"/);
  const pageView = readFileSync(new URL('../../src/components/AppPageView.vue', import.meta.url), 'utf8');
  const pageTransition = elements(pageView, node => node.tag === 'Transition')[0];
  assert.ok(pageTransition);
  assert.equal(binding(pageTransition, 'css'), 'pageTransitionEnabled');
  const css = readStylesheet(new URL('../../src/styles/components.css', import.meta.url));
  assert.match(css, /-webkit-backdrop-filter: blur\(6px\); backdrop-filter: blur\(6px\);/);
  assert.match(css, /\.n-base-selection--disabled \.n-base-suffix \{ cursor: not-allowed; \}/);
  assert.match(css, /\.n-modal-mask, \.n-drawer-mask:not\(\.n-drawer-mask--invisible\) \{ background-color: rgba\(0, 0, 0, \.3\); \}/);
  const transitions = readStylesheet(new URL('../../src/styles/transitions.css', import.meta.url));
  assert.match(transitions, /prefers-reduced-motion: reduce/);
  assert.match(transitions, /\.page-fade-enter-active, \.page-fade-leave-active \{ transition: none;/);
});

test('theme switches preserve current appearance when disabling automatic mode', () => {
  const themeMode = { value: 'system' }, resolvedTheme = { value: 'dark' };
  const context = {
    exports: {}, defineProps() {}, defineEmits: () => () => {},
    require: name => name === '../ui/theme'
      ? { useTheme: () => ({ themeMode, resolvedTheme, setTheme: value => { themeMode.value = value; } }) }
      : name === '../ui/panelEffects' ? { usePanelEffects: () => ({}) } : {},
  };
  runInNewContext(loadScriptModule(new URL('../../src/components/AppearanceSettingsDrawer.vue', import.meta.url), 'setAutomaticTheme, setDarkMode'), context);
  context.exports.setAutomaticTheme(false);
  assert.equal(themeMode.value, 'dark');
  context.exports.setDarkMode(false);
  assert.equal(themeMode.value, 'light');
  context.exports.setDarkMode(true);
  assert.equal(themeMode.value, 'dark');
  context.exports.setAutomaticTheme(true);
  assert.equal(themeMode.value, 'system');
  resolvedTheme.value = 'light';
  context.exports.setAutomaticTheme(false);
  assert.equal(themeMode.value, 'light');
  const source = readFileSync(new URL('../../src/components/AppearanceSettingsDrawer.vue', import.meta.url), 'utf8');
  assert.match(source, /title="主题"/);
  assert.match(source, /<NCollapseTransition :show="themeMode !== 'system'"/);
  assert.doesNotMatch(source, /themeOptions|title="主题设置"/);
});
