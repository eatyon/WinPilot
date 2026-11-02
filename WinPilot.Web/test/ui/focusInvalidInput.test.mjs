import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';

const script = loadScriptModule(new URL('../../src/ui/focusInvalidInput.ts', import.meta.url));

test('validation animates the visible control while keeping focus on its input', () => {
  for (const kind of ['group', 'input', 'native', 'explicit']) {
    const calls = [];
    const outer = {
      scrollIntoView: options => { assert.equal(options.behavior, 'smooth'); assert.equal(options.block, 'center'); calls.push('outer:scroll'); },
      animate: () => calls.push('outer:animate'),
    };
    const input = {
      closest: selector => {
        calls.push(selector);
        return (kind === 'group' && selector === '.n-input-group') ||
          (kind === 'input' && selector === '.n-input') ? outer : null;
      },
      scrollIntoView: options => { assert.equal(options.behavior, 'smooth'); assert.equal(options.block, 'center'); calls.push('input:scroll'); },
      focus: options => { assert.equal(options.preventScroll, true); calls.push('input:focus'); },
      animate: () => calls.push('input:animate'),
    };
    const context = { exports: {}, window: { matchMedia: () => ({ matches: false }) } };
    runInNewContext(script, context);
    context.exports.focusInvalidInput(input, kind === 'explicit' ? outer : undefined);
    const target = kind === 'native' ? 'input' : 'outer';
    assert.deepEqual(calls.filter(call => !call.startsWith('.')), [`${target}:scroll`, 'input:focus', `${target}:animate`]);
    if (kind === 'group') assert.deepEqual(calls.filter(call => call.startsWith('.')), ['.n-input-group']);
    if (kind === 'explicit') assert.ok(!calls.some(call => call.startsWith('.')));
  }
});

test('reduced motion preserves focus and scrolling without animation, and missing inputs are ignored', () => {
  const calls = [];
  const context = { exports: {}, window: { matchMedia: () => ({ matches: true }) } };
  runInNewContext(script, context);
  const outer = { scrollIntoView: options => { assert.equal(options.behavior, 'instant'); calls.push('scroll'); }, animate: () => calls.push('animate') };
  context.exports.focusInvalidInput({ closest: () => outer, focus: () => calls.push('focus') });
  context.exports.focusInvalidInput(null);
  context.exports.focusInvalidInput(undefined);
  assert.deepEqual(calls, ['scroll', 'focus']);
});

test('disabled interaction animations preserve validation focus with instant scrolling', () => {
  const calls = [];
  const context = {
    exports: {}, window: { matchMedia: () => ({ matches: false }) },
    document: { documentElement: { classList: { contains: name => name === 'interaction-animations-disabled' } } },
  };
  runInNewContext(script, context);
  const target = {
    scrollIntoView: options => { assert.equal(options.behavior, 'instant'); calls.push('scroll'); },
    animate: () => calls.push('animate'),
  };
  context.exports.focusInvalidInput({ closest: () => target, focus: () => calls.push('focus') });
  assert.deepEqual(calls, ['scroll', 'focus']);
});
