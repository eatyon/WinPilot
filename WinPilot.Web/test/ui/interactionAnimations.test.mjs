import { readStylesheet } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';
import postcss from 'postcss';

const read = path => path.endsWith('.css') ? readStylesheet(new URL(`../../src/${path}`, import.meta.url)) : readFileSync(new URL(`../../src/${path}`, import.meta.url), 'utf8');

test('interaction switch disables transitions globally while exempting page transitions and loading animations', () => {
  const rules = [];
  postcss.parse(read('styles/transitions.css')).walkRules(rule => {
    if (rule.selector.includes('.interaction-animations-disabled')) rules.push(rule);
  });
  assert.equal(rules.length, 2);
  assert.match(rules[0].selector, /:not\(\[data-page-transition\]\)/);
  assert.match(rules[0].selector, /:not\(\.n-loading-bar-container, \.n-loading-bar\)/);
  const declarations = Object.fromEntries(rules[0].nodes.map(node => [node.prop, node.value]));
  assert.deepEqual(declarations, { 'transition-duration': '0s', 'transition-delay': '0s', 'scroll-behavior': 'auto' });
  assert.ok(rules[0].nodes.every(node => node.important));
  assert.doesNotMatch(rules[0].toString(), /animation-duration|animation-name/);
  assert.equal(rules[1].selector, '.interaction-animations-disabled .n-base-wave');
  assert.match(read('components/AppPageView.vue'), /class="route-page" data-page-transition/);
  assert.match(read('features/settings/SettingsView.vue'), /:key="activeKey" data-page-transition/);
  assert.match(read('features/tasks/TaskConfigTransition.vue'), /:css="interactionAnimationEnabled"\s+:mode="interactionAnimationEnabled \? 'out-in' : undefined"/);
});

test('back to top follows the effective animation state shared by system and user preferences', () => {
  const source = read('App.vue');
  assert.match(source, /@click.capture="handleBackTopClick"/);
  const handler = source.match(/function handleBackTopClick\(event: MouseEvent\) \{[\s\S]*?\n\}/)?.[0];
  assert.ok(handler);
  const calls = [];
  const classes = new Set();
  const context = {
    exports: {}, document: { documentElement: { classList: { contains: name => classes.has(name) } } },
    getPageScroller: () => ({ scrollTo: options => calls.push(options) }),
  };
  runInNewContext(ts.transpileModule(`${handler}\nexport { handleBackTopClick };`, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText, context);
  const event = { stopImmediatePropagation: () => calls.push('stop') };
  context.exports.handleBackTopClick(event);
  assert.equal(calls.length, 0);
  classes.add('interaction-animations-disabled');
  context.exports.handleBackTopClick(event);
  assert.equal(calls[0], 'stop');
  assert.equal(calls[1].top, 0);
  assert.equal(calls[1].behavior, 'instant');
  classes.clear();
  context.exports.handleBackTopClick(event);
  assert.equal(calls.length, 2);
});
