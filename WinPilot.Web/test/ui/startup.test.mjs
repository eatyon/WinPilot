import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';

test('initial theme is applied before the application loads without writing preferences', () => {
  const html = readFileSync(new URL('../../index.html', import.meta.url), 'utf8');
  const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
  assert.ok(html.indexOf(script) < html.indexOf('type="module"'));
  for (const mode of ['light', 'dark', 'system', null, 'invalid']) {
    const root = { dataset: {} };
    runInNewContext(script, {
      document: { documentElement: root },
      localStorage: { getItem: key => { assert.equal(key, 'winpilot.theme'); return mode; } },
    });
    assert.equal(root.dataset.theme, ['light', 'dark'].includes(mode) ? mode : undefined);
  }
  assert.doesNotThrow(() => runInNewContext(script, {
    localStorage: { getItem: () => { throw new Error('Storage unavailable'); } },
  }));
  const css = readFileSync(new URL('../../src/style.css', import.meta.url), 'utf8');
  for (const color of ['#f5f6f8', '#171b21']) {
    assert.ok(html.includes(color));
    assert.ok(css.includes(`--canvas: ${color}`));
  }
});

test('application waits for the initial route and still mounts when navigation fails', async () => {
  const code = loadScriptModule(new URL('../../src/main.ts', import.meta.url));
  for (const failed of [false, true]) {
    const events = [];
    let finish;
    const ready = new Promise((resolve, reject) => { finish = failed ? reject : resolve; });
    const router = { isReady: () => ready };
    const app = {
      use: value => { assert.equal(value, router); events.push('router'); return app; },
      mount: target => { assert.equal(target, '#app'); events.push('mount'); },
    };
    runInNewContext(code, {
      exports: {},
      require: name => {
        if (name === 'vue') return { createApp: () => app };
        if (name === './router') return { router };
        if (name === './ui/theme') return { initializeTheme: () => events.push('theme') };
        return {};
      },
    });
    assert.deepEqual(events, ['theme', 'router']);
    finish(failed ? new Error('Navigation failed') : undefined);
    await Promise.resolve();
    assert.deepEqual(events, ['theme', 'router', 'mount']);
  }
});
