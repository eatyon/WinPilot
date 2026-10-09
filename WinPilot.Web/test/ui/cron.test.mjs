import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';

function fixture(expression) {
  const code = loadScriptModule(new URL('../../src/features/tasks/CronEditor.vue', import.meta.url),
    'mode, frequency, interval, weekday, generated');
  const emissions = [];
  const context = {
    exports: {}, defineProps: () => ({ modelValue: expression }),
    defineEmits: () => (_, value) => emissions.push(value),
    require: name => name === 'vue' ? vue : {},
  };
  const scope = vue.effectScope();
  scope.run(() => runInNewContext(code, context));
  return { subject: context.exports, emissions, dispose: () => scope.stop() };
}

test('simple cron restores hourly intervals and normalizes Sunday without changing the day', () => {
  for (const [expression, expected] of [['30 */2 * * *', '30 */2 * * *'], ['30 8 * * 0', '30 8 * * 7']]) {
    const f = fixture(expression);
    try {
      assert.equal(f.subject.mode.value, 'simple');
      assert.equal(f.subject.generated.value, expected);
      assert.deepEqual(f.emissions, [expected]);
    } finally { f.dispose(); }
  }
});

test('unsupported cron stays advanced and never emits a replacement', () => {
  const f = fixture('0 8 * * 1-5');
  try {
    assert.equal(f.subject.mode.value, 'advanced');
    assert.deepEqual(f.emissions, []);
  } finally { f.dispose(); }
});
