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

export function combinePageSignal(page: AbortSignal, request?: AbortSignal) {
  if (!request || request === page) return { signal: page, dispose: () => {} };
  const controller = new AbortController();
  const pageAborted = () => controller.abort(page.reason);
  const requestAborted = () => controller.abort(request.reason);
  page.addEventListener('abort', pageAborted, { once: true });
  request.addEventListener('abort', requestAborted, { once: true });
  if (page.aborted) pageAborted();
  else if (request.aborted) requestAborted();
  return {
    signal: controller.signal,
    dispose() {
      page.removeEventListener('abort', pageAborted);
      request.removeEventListener('abort', requestAborted);
    },
  };
}
