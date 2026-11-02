import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';

test('existing toast calls reach Message and teardown removes the listener and messages', () => {
  const calls = [];
  let mounted, unmounted, destroyed = 0;
  const context = {
    exports: {}, window: new EventTarget(), CustomEvent,
    require(name) {
      if (name === 'vue') return {
        onMounted: callback => { mounted = callback; },
        onUnmounted: callback => { unmounted = callback; },
      };
      if (name === 'naive-ui') return {
        useMessage: () => ({
          create: (content, options) => calls.push({ content, ...options }),
          destroyAll: () => { destroyed++; },
        }),
      };
      throw new Error(`Unexpected import: ${name}`);
    },
  };
  runInNewContext(loadScriptModule(new URL('../../src/ui/toast.ts', import.meta.url)), context);
  const showToast = context.exports.showToast;
  runInNewContext(loadScriptModule(new URL('../../src/components/AppToast.vue', import.meta.url)), context);
  mounted();
  showToast('标签已启动', 'success');
  showToast('操作失败');
  showToast('');
  context.window.dispatchEvent(new CustomEvent('winpilot:toast', { detail: { message: 123 } }));
  assert.deepEqual(calls.map(({ content, type }) => [content, type]), [['标签已启动', 'success'], ['操作失败', 'error']]);
  unmounted();
  showToast('不应再显示');
  assert.equal(calls.length, 2);
  assert.equal(destroyed, 1);
});
