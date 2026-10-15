export function combineRequestSignal(owner: AbortSignal, request?: AbortSignal) {
  if (!request || request === owner) return { signal: owner, dispose: () => {} };
  const controller = new AbortController();
  const ownerAborted = () => controller.abort(owner.reason);
  const requestAborted = () => controller.abort(request.reason);
  owner.addEventListener('abort', ownerAborted, { once: true });
  request.addEventListener('abort', requestAborted, { once: true });
  if (owner.aborted) ownerAborted();
  else if (request.aborted) requestAborted();
  return {
    signal: controller.signal,
    dispose() {
      owner.removeEventListener('abort', ownerAborted);
      request.removeEventListener('abort', requestAborted);
    },
  };
}
