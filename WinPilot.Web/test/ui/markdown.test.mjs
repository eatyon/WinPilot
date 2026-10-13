import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import MarkdownIt from 'markdown-it';
import { loadScriptModule } from '../support/scriptModule.mjs';

function fixture() {
  let options;
  const context = {
    exports: {},
    require: name => name === 'markdown-it' ? { __esModule: true, default: MarkdownIt } : {
      __esModule: true,
      default: { sanitize(html, config) { options = config; return html; } },
    },
  };
  runInNewContext(loadScriptModule(new URL('../../src/ui/markdown.ts', import.meta.url)), context);
  return { render: context.exports.renderMarkdown, options: () => options };
}

test('markdown links use protected new tabs and the sanitizer explicitly retains their target', async () => {
  const f = fixture();
  const html = await f.render('[Site](https://example.com) [Mail](mailto:test@example.com)');
  assert.match(html, /href="https:\/\/example.com" target="_blank" rel="noopener noreferrer"/);
  assert.match(html, /href="mailto:test@example.com" target="_blank" rel="noopener noreferrer"/);
  assert.deepEqual(Array.from(f.options().ADD_ATTR), ['target']);
  assert.deepEqual(Array.from(f.options().FORBID_TAGS), ['img', 'svg', 'iframe', 'form', 'input', 'button']);
});

test('markdown still escapes raw HTML, rejects unsafe links and renders images as text', async () => {
  const f = fixture();
  const html = await f.render('<script>alert(1)</script>\n\n[Bad](javascript:alert(1))\n\n![Screenshot](https://example.com/a.png)');
  assert.ok(html.includes('&lt;script&gt;'));
  assert.doesNotMatch(html, /<script\b|<img\b|href="javascript:/i);
  assert.ok(html.includes('Screenshot'));
});
