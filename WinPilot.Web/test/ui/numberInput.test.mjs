import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';

test('number input adapts library numeric values and keeps range checks in business validation', () => {
  const values = [];
  const attrs = { min: 1, max: 10, id: 'duration', disabled: true, inputmode: 'numeric' };
  const context = {
    exports: {},
    defineOptions: () => {},
    defineProps: () => ({ modelValue: 5 }),
    defineEmits: () => (_, value) => values.push(value),
    require: name => name === 'vue' ? { useAttrs: () => attrs } : {},
  };
  runInNewContext(loadScriptModule(new URL('../../src/components/AppNumberInput.vue', import.meta.url),
    'updateValue, inputProps, controlAttrs'), context);
  const input = context.exports;
  for (const value of [null, 2.5, 12, -1, 1000]) input.updateValue(value);
  assert.deepEqual(values, ['', 2.5, 12, -1, 1000]);
  assert.equal(input.inputProps().min, '1');
  assert.equal(input.inputProps().id, 'duration');
  assert.equal(input.inputProps().inputmode, 'numeric');
  assert.equal(input.controlAttrs().inputmode, undefined);
  assert.equal(input.controlAttrs().id, undefined);
  assert.equal(input.controlAttrs().disabled, true);
  attrs.min = 3;
  attrs['aria-invalid'] = true;
  assert.equal(input.inputProps().min, '3');
  assert.equal(input.inputProps()['aria-invalid'], true);
  attrs['aria-invalid'] = false;
  assert.equal(input.inputProps()['aria-invalid'], undefined);
});
