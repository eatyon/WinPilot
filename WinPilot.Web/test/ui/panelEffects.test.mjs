import { readStylesheet } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

test('image preview shares the opt-in mask blur without blurring the image or toolbar', () => {
  const css = readStylesheet(new URL('../../src/styles/components.css', import.meta.url));
  assert.match(css, /\.dialog-blur-enabled :is\(\.n-modal-mask, \.n-drawer-mask, \.n-image-preview-overlay\) \{ -webkit-backdrop-filter: blur\(6px\); backdrop-filter: blur\(6px\); \}/);
  assert.doesNotMatch(css, /\.dialog-blur-enabled[^{}]*(?:\.n-image-preview-container|\.n-image-preview-toolbar|\.n-image-preview\b(?!-overlay))[^{}]*\{/);
});

test('panel effects preserve existing interaction animations and persist independent preferences', () => {
  const classes = new Set();
  let stored = null, unavailable = false;
  const motionListeners = new Set();
  const reducedMotion = { matches: true, addEventListener: (_, listener) => motionListeners.add(listener) };
  const context = {
    exports: {},
    window: { matchMedia: query => {
      assert.equal(query, '(prefers-reduced-motion: reduce)');
      return reducedMotion;
    } },
    require: () => ({ ref: value => ({ value }), readonly: value => value }),
    document: { documentElement: { classList: { toggle(name, enabled) { enabled ? classes.add(name) : classes.delete(name); } } } },
    localStorage: {
      getItem: () => { if (unavailable) throw new Error('blocked'); return stored; },
      setItem: (_, value) => { if (unavailable) throw new Error('blocked'); stored = value; },
    },
  };
  const source = readFileSync(new URL('../../src/ui/panelEffects.ts', import.meta.url), 'utf8');
  runInNewContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText, context);
  const { initializePanelEffects, usePanelEffects } = context.exports;
  initializePanelEffects();
  const effects = usePanelEffects();
  assert.ok(classes.has('interaction-animations-disabled'));
  reducedMotion.matches = false;
  for (const listener of motionListeners) listener();
  assert.equal(effects.dialogBlurEnabled.value, false);
  assert.equal(effects.pageTransitionEnabled.value, true);
  assert.equal(effects.interactionAnimationEnabled.value, true);
  assert.equal(effects.loadingBarEnabled.value, true);
  assert.ok(!classes.has('interaction-animations-disabled'));
  reducedMotion.matches = true;
  for (const listener of motionListeners) listener();
  assert.ok(classes.has('interaction-animations-disabled'));
  assert.equal(effects.interactionAnimationEnabled.value, true);
  assert.equal(stored, null);
  reducedMotion.matches = false;
  for (const listener of motionListeners) listener();
  assert.ok(!classes.has('interaction-animations-disabled'));
  effects.setDialogBlurEnabled(true);
  assert.ok(classes.has('dialog-blur-enabled'));
  effects.setPageTransitionEnabled(true);
  assert.deepEqual(JSON.parse(stored), { dialogBlurEnabled: true, pageTransitionEnabled: true, interactionAnimationEnabled: true, loadingBarEnabled: true, pageTransitionEffect: 'fade' });
  effects.setLoadingBarEnabled(false);
  initializePanelEffects();
  assert.equal(effects.loadingBarEnabled.value, false);
  assert.equal(effects.interactionAnimationEnabled.value, true);
  assert.equal(effects.pageTransitionEnabled.value, true);
  effects.setInteractionAnimationEnabled(false);
  assert.ok(classes.has('interaction-animations-disabled'));
  reducedMotion.matches = true;
  for (const listener of motionListeners) listener();
  reducedMotion.matches = false;
  for (const listener of motionListeners) listener();
  assert.ok(classes.has('interaction-animations-disabled'));
  assert.equal(motionListeners.size, 1);
  assert.equal(effects.pageTransitionEnabled.value, true);
  initializePanelEffects();
  assert.equal(effects.interactionAnimationEnabled.value, false);
  assert.equal(effects.pageTransitionEffect.value, 'fade');
  effects.setInteractionAnimationEnabled(true);
  assert.ok(!classes.has('interaction-animations-disabled'));
  for (const effect of ['fade-slide', 'fade', 'slide', 'scale']) {
    effects.setPageTransitionEffect(effect);
    initializePanelEffects();
    assert.equal(effects.pageTransitionEffect.value, effect);
    assert.equal(effects.pageTransitionEnabled.value, true);
  }
  effects.setPageTransitionEffect('invalid');
  assert.equal(effects.pageTransitionEffect.value, 'scale');
  initializePanelEffects();
  assert.equal(effects.pageTransitionEnabled.value, true);
  effects.setPageTransitionEnabled(false);
  initializePanelEffects();
  assert.equal(effects.pageTransitionEnabled.value, false);
  stored = '{';
  initializePanelEffects();
  assert.equal(effects.loadingBarEnabled.value, true);
  assert.equal(effects.pageTransitionEnabled.value, true);
  assert.equal(effects.interactionAnimationEnabled.value, true);
  assert.equal(effects.pageTransitionEffect.value, 'fade');
  stored = '{"dialogBlurEnabled":"true","pageTransitionEnabled":1,"interactionAnimationEnabled":"false"}';
  initializePanelEffects();
  assert.equal(effects.dialogBlurEnabled.value, false);
  assert.equal(effects.pageTransitionEnabled.value, true);
  assert.equal(effects.interactionAnimationEnabled.value, true);
  stored = '{"dialogBlur":true,"pageTransition":true,"interactionAnimation":false,"loadingBar":false}';
  initializePanelEffects();
  assert.equal(effects.dialogBlurEnabled.value, false);
  assert.equal(effects.pageTransitionEnabled.value, true);
  assert.equal(effects.interactionAnimationEnabled.value, true);
  assert.equal(effects.loadingBarEnabled.value, true);
  unavailable = true;
  effects.setInteractionAnimationEnabled(false);
  assert.ok(classes.has('interaction-animations-disabled'));
  effects.setDialogBlurEnabled(true);
  assert.ok(classes.has('dialog-blur-enabled'));
  initializePanelEffects();
  assert.equal(effects.pageTransitionEnabled.value, true);
  assert.ok(!classes.has('dialog-blur-enabled'));
  assert.ok(!classes.has('interaction-animations-disabled'));
  assert.equal(motionListeners.size, 1);
});
