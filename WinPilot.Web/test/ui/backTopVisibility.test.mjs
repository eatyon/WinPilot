import assert from 'node:assert/strict';
import test from 'node:test';
import { createBackTopVisibility } from '../../src/ui/backTopVisibility.ts';

test('back top follows accumulated scroll direction above the visibility threshold', () => {
  const state = createBackTopVisibility();
  assert.equal(state.update(400), false);
  assert.equal(state.update(392), false);
  assert.equal(state.update(384), true);
  assert.equal(state.update(390), true);
  assert.equal(state.update(400), false);
  assert.equal(state.update(380), true);
  assert.equal(state.update(179), false);
  assert.equal(state.update(-10), false);
  state.reset();
  assert.equal(state.update(500), false);
});
