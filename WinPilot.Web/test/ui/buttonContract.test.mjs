import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import test from 'node:test';
import { elements, attribute } from '../support/template.mjs';

test('login and terminal forms declare submit buttons using the library contract', () => {
  for (const page of ['auth/LoginView.vue', 'terminal/TerminalView.vue']) {
    const source = readFileSync(new URL('../../src/features/' + page, import.meta.url), 'utf8');
    assert.ok(elements(source, node => node.tag === 'NButton' && attribute(node, 'attr-type') === 'submit').length > 0, page);
  }
  const root = new URL('../../src/', import.meta.url);
  for (const file of readdirSync(root, { recursive: true }).filter(file => file.endsWith('.vue'))) {
    assert.doesNotMatch(readFileSync(new URL(file.replaceAll('\\', '/'), root), 'utf8'), /native-type=/, file);
  }
});
