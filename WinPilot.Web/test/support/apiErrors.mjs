import { runInNewContext } from 'node:vm';
import { loadScriptModule } from './scriptModule.mjs';

const exports = {};
runInNewContext(loadScriptModule(new URL('../../src/api/client.ts', import.meta.url)), {
  exports, require: () => ({}), DOMException, Error,
});
export const { apiErrorMessage, readApiError, userErrorMessage, WinPilotConnectionError } = exports;
