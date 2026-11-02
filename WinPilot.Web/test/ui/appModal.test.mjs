import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';

test('available variables inherits the shared click-outside closing behavior', () => {
  const source = readFileSync(new URL('../../src/features/tasks/TaskVariableHelp.vue', import.meta.url), 'utf8');
  assert.doesNotMatch(source, /:dismiss-on-backdrop="false"|:dismissible="false"/);
  assert.match(source, /<AppModal title="可用变量"[^>]*@close="emit\('close'\)"/);
  const modal = readFileSync(new URL('../../src/components/AppModal.vue', import.meta.url), 'utf8');
  assert.match(modal, /dismissOnBackdrop: true/);
  assert.match(modal, /:mask-closable="dismissible && dismissOnBackdrop"/);
});

function modalFixture(show) {
  const props = { dismissible: true, show };
  const events = [];
  const context = {
    exports: {}, defineProps: () => props, withDefaults: value => value,
    defineEmits: () => event => events.push(event),
    require: name => name === 'vue'
      ? { ...vue, onMounted() {}, onBeforeUnmount() {} }
      : {},
  };
  runInNewContext(loadScriptModule(new URL('../../src/components/AppModal.vue', import.meta.url),
    'requestClose, afterLeave, shown'), context);
  return { props, events, ...context.exports };
}

test('uncontrolled modal requests removal only after its native leave animation', () => {
  const modal = modalFixture(undefined);
  modal.requestClose();
  assert.equal(modal.shown.value, false);
  assert.deepEqual(modal.events, []);
  modal.afterLeave();
  assert.deepEqual(modal.events, ['close', 'after-leave']);
});

test('controlled modal requests hiding immediately and cleanup separately', () => {
  const modal = modalFixture(true);
  modal.requestClose();
  assert.deepEqual(modal.events, ['close']);
  modal.props.show = false;
  assert.equal(modal.shown.value, false);
  modal.afterLeave();
  assert.deepEqual(modal.events, ['close', 'after-leave']);
});

test('non-dismissible modal ignores user closing', () => {
  const modal = modalFixture(undefined);
  modal.props.dismissible = false;
  modal.requestClose();
  assert.equal(modal.shown.value, true);
  assert.deepEqual(modal.events, []);
});

test('shared modal uses the header slot without a title prop overriding its extra content', () => {
  const source = readFileSync(new URL('../../src/components/AppModal.vue', import.meta.url), 'utf8');
  assert.doesNotMatch(source, /:title="title"/);
  assert.match(source, /<template #header>[\s\S]*\{\{ title \}\}<slot name="title-extra"\s*\/>/);
  assert.match(source, /#header-extra>[\s\S]*<slot name="header-actions"\s*\/>/);
});

test('shared modal leaves all corner radii to the library theme on mobile', () => {
  for (const file of ['style.css', 'styles/components.css', 'components/AppModal.vue']) {
    const styles = readFileSync(new URL(`../../src/${file}`, import.meta.url), 'utf8');
    assert.doesNotMatch(styles, /\.app-modal\s*\{[^}]*border-radius\s*:/);
  }
});

test('shared modal locks the real workspace and releases it without a second focus trap', () => {
  let mount, dispose, locked, releases = 0;
  const scroller = {};
  const context = {
    exports: {}, defineProps: () => ({}), withDefaults: value => value, defineEmits: () => () => {},
    require: name => name === 'vue' ? {
      ...vue,
      onMounted: callback => { mount = callback; }, onBeforeUnmount: callback => { dispose = callback; },
    } : { getPageScroller: () => scroller, lockScroll: target => { locked = target; return () => { releases++; }; } },
  };
  runInNewContext(loadScriptModule(new URL('../../src/components/AppModal.vue', import.meta.url)), context);
  mount();
  assert.equal(locked, scroller);
  dispose();
  assert.equal(releases, 1);
  const source = readFileSync(new URL('../../src/components/AppModal.vue', import.meta.url), 'utf8');
  assert.match(source, /<NModal/);
  assert.match(source, /content-scrollable/);
  assert.match(source, /:mask-closable="dismissible && dismissOnBackdrop"/);
  assert.match(source, /:close-on-esc="dismissible"/);
  assert.doesNotMatch(source, /manageModalFocus|manageModalTouch|addEventListener/);
});

test('shared modal reserves focus space without increasing header or footer gaps', () => {
  const source = readFileSync(new URL('../../src/components/AppModal.vue', import.meta.url), 'utf8');
  assert.match(source, /:header-style="\{ paddingBottom: 'calc\(var\(--n-padding-bottom\) - 4px\)' \}"/);
  assert.match(source, /:content-style="\{ paddingTop: '4px', paddingBottom: \$slots.actions \? 'calc\(var\(--n-padding-bottom\) - 4px\)' : undefined \}"/);
  assert.match(source, /:footer-style="\{ paddingTop: '4px' \}"/);
});
