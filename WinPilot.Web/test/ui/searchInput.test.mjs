import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import test from 'node:test';
import { elements, attribute, binding } from '../support/template.mjs';

const root = new URL('../../src/', import.meta.url);
const searchInputs = [];
for (const file of readdirSync(root, { recursive: true })) {
  if (!file.endsWith('.vue')) continue;
  const source = readFileSync(new URL(file.replaceAll('\\', '/'), root), 'utf8');
  for (const node of elements(source, node => node.tag === 'NInput' && /搜索/.test(
    `${attribute(node, 'placeholder') ?? ''} ${attribute(node, 'aria-label') ?? ''} ${binding(node, 'input-props') ?? ''}`,
  ))) searchInputs.push({ file, node });
}

test('all host search inputs use the library clear control without native search cancel buttons', () => {
  assert.ok(searchInputs.length > 0, 'search input scan must find controls');
  for (const { file, node } of searchInputs) {
    assert.ok(node.props.some(prop => prop.type === 6 && prop.name === 'clearable'), file);
    const props = binding(node, 'input-props') ?? '';
    assert.doesNotMatch(props, /type\s*:\s*['"]search['"]/, file);
    assert.notEqual(attribute(node, 'type'), 'search', file);
    assert.match(props, /role: 'searchbox'/, file);
    assert.match(props, /enterkeyhint: 'search'/, file);
  }
});
