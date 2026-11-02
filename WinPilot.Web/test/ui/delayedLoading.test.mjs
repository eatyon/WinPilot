import assert from 'node:assert/strict';
import test from 'node:test';
import { effectScope, ref } from 'vue';
import { useDelayedLoading } from '../../src/ui/useDelayedLoading.ts';

test('loading feedback skips fast requests and clears immediately on completion', context => {
  context.mock.timers.enable({ apis: ['setTimeout'] });
  const scope = effectScope();
  const loading = ref(false);
  const visible = scope.run(() => useDelayedLoading(() => loading.value));
  try {
    loading.value = true;
    context.mock.timers.tick(149);
    assert.equal(visible.value, false);
    loading.value = false;
    context.mock.timers.tick(200);
    assert.equal(visible.value, false);
    loading.value = true;
    context.mock.timers.tick(150);
    assert.equal(visible.value, true);
    loading.value = false;
    assert.equal(visible.value, false);
  } finally { scope.stop(); }
});

test('a new request gets its own delay and disposal cancels its pending timer', context => {
  context.mock.timers.enable({ apis: ['setTimeout'] });
  const scope = effectScope();
  const loading = ref(true);
  const visible = scope.run(() => useDelayedLoading(() => loading.value));
  context.mock.timers.tick(100);
  loading.value = false;
  loading.value = true;
  context.mock.timers.tick(100);
  assert.equal(visible.value, false);
  scope.stop();
  context.mock.timers.tick(200);
  assert.equal(visible.value, false);
});
