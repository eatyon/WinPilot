import { computed, ref } from 'vue';
import { runInNewContext } from 'node:vm';
import { createBackTopVisibility } from '../../src/ui/backTopVisibility.ts';
import { loadScriptModule } from './scriptModule.mjs';

const code = loadScriptModule(new URL('../../src/App.vue', import.meta.url),
  'handleBackTopClick, sidebarCollapsed, updateSidebarWidth, setSidebarCollapsed');

export function createAppShell(overrides = {}) {
  const context = {
    exports: {}, require: () => context, computed, ref,
    watch() {}, onMounted() {}, onUnmounted() {},
    useRoute: () => ({ path: '/home', meta: {}, matched: [] }),
    createBackTopVisibility,
    readSidebarCollapsed: () => false, saveSidebarCollapsed() {},
    window: { matchMedia: () => ({ matches: false }) },
    ...overrides,
  };
  runInNewContext(code, context);
  return context.exports;
}
