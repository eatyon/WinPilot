import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { elements, attribute } from '../support/template.mjs';

test('login and terminal forms contain their submit buttons', () => {
  for (const page of ['auth/LoginView.vue', 'terminal/TerminalView.vue']) {
    const source = readFileSync(new URL('../../src/features/' + page, import.meta.url), 'utf8');
    const forms = elements(source, node => node.tag === 'form');
    assert.ok(forms.length > 0, page);
    const hasSubmit = node => (node.tag === 'NButton' && attribute(node, 'attr-type') === 'submit')
      || (node.children ?? []).some(hasSubmit);
    for (const form of forms) assert.ok(hasSubmit(form), page);
  }
});
