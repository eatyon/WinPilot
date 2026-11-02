import { readFeatureStyles } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { compileTemplate, parse } from '@vue/compiler-sfc';
import { binding, elements } from '../support/template.mjs';

const url = new URL('../../src/features/tasks/TaskConfigTransition.vue', import.meta.url);
const source = readFileSync(url, 'utf8');

test('task configuration closes the old keyed content before opening the new content', () => {
  assert.match(source, /<Transition\s+name="task-action"\s+:css="interactionAnimationEnabled"\s+:mode="interactionAnimationEnabled \? 'out-in' : undefined"/);
  assert.match(source, /<div :key="value" class="task-config-transition task-action-transition">/);
  assert.match(source, /class="task-config-transition-content task-action-content"/);
  assert.match(source, /@before-leave="\(element\) => element.setAttribute\('inert', ''\)"/);
  assert.match(source, /@leave-cancelled="\(element\) => element.removeAttribute\('inert'\)"/);
  const { descriptor } = parse(source);
  assert.deepEqual(compileTemplate({ source: descriptor.template.content, filename: 'TaskConfigTransition.vue', id: 'task-config' }).errors, []);
});

test('task configuration reuses collapse styles without height tracking or fixed sizing', () => {
  assert.doesNotMatch(source, /ResizeObserver|animate\(|setTimeout|requestAnimationFrame|onBeforeUpdate|onUpdated|style\.height|appear/);
  const css = readFeatureStyles();
  assert.match(css, /\.task-action-transition\s*\{[^}]*grid-template-rows: 1fr/);
  assert.match(css, /\.task-action-enter-from, \.task-action-leave-to\s*\{[^}]*grid-template-rows: 0fr/);
  assert.match(css, /@media \(prefers-reduced-motion: reduce\)\s*\{[^{}]*\.task-action-enter-active[^{}]*\{[^{}]*transition: none/);
});

test('configuration transitions follow only the main type and keep type selectors outside', () => {
  for (const [file, key] of [
    ['TasksView.vue', 'editor.trigger.type'],
    ['TaskActionEditor.vue', 'action.type'],
  ]) {
    const page = readFileSync(new URL(`../../src/features/tasks/${file}`, import.meta.url), 'utf8');
    const transitions = elements(page, node => node.tag === 'TaskConfigTransition');
    assert.equal(transitions.length, 1);
    assert.equal(binding(transitions[0], 'value'), key);
    assert.doesNotMatch(transitions[0].loc.source, /data-task-field="type"|v-model="editor.trigger.type"/);
    const { descriptor } = parse(page);
    assert.deepEqual(compileTemplate({ source: descriptor.template.content, filename: file, id: 'task-editor' }).errors, []);
  }
});
