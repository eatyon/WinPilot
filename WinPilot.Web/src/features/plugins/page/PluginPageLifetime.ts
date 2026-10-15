export function createPluginPageLifetime(reportError: (error: unknown) => void) {
  const controller = new AbortController();
  const cleanups = new Set<() => void>();
  const run = (cleanup: () => void) => {
    try { cleanup(); } catch (error) { reportError(error); }
  };
  return {
    signal: controller.signal,
    onDispose(cleanup: () => void) {
      if (controller.signal.aborted) run(cleanup);
      else cleanups.add(cleanup);
      return () => cleanups.delete(cleanup);
    },
    dispose() {
      if (controller.signal.aborted) return;
      controller.abort();
      const pending = [...cleanups].reverse();
      cleanups.clear();
      for (const cleanup of pending) run(cleanup);
    },
  };
}

export { combineRequestSignal as combinePageSignal } from '../../../api/requestSignal.ts';
