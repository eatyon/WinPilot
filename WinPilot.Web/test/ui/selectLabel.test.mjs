import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { h } from 'vue';
import { loadScriptModule } from '../support/scriptModule.mjs';
import { textPopoverStyle } from '../../src/ui/textPopoverStyle.ts';

function renderer(canHover = true) {
  const context = {
    exports: {},
    window: { matchMedia: query => {
      assert.equal(query, '(hover: hover) and (pointer: fine)');
      return { matches: canHover };
    } },
    require: name => name === 'vue' ? { h } : name === './textPopoverStyle.ts' ? { textPopoverStyle } : { NEllipsis: { name: 'NEllipsis' } },
  };
  runInNewContext(loadScriptModule(new URL('../../src/ui/selectLabel.ts', import.meta.url)), context);
  return context.exports;
}

test('select labels delegate overflow hints to the library without native title attributes', () => {
  const { renderSelectLabel: render } = renderer();
  for (const label of ['启动程序', 'C:\\很长的目录\\'.repeat(30), 25, '']) {
    const option = { label, value: 'value' };
    const node = render(option, true);
    assert.equal(node.type.name, 'NEllipsis');
    assert.equal(node.props.title, undefined);
    assert.equal(node.children.default(), String(label));
    assert.equal(node.props.tooltip.disabled, false);
    assert.equal(node.props.tooltip.style.maxWidth, 'min(320px, calc(100vw - 32px))');
    assert.equal(node.props.tooltip.style, textPopoverStyle);
    assert.equal(option.label, label);
  }
});

test('touch devices keep labels without sticky hover hints', () => {
  const node = renderer(false).renderSelectLabel({ label: '启动程序', value: 'program' }, true);
  assert.equal(node.props.tooltip.disabled, true);
  assert.equal(node.children.default(), '启动程序');
});

test('fallback values and custom label functions preserve content and selected state', () => {
  const { renderSelectLabel: render } = renderer();
  assert.equal(render({ value: 25 }, true).children.default(), '25');
  const option = { value: 'custom', label: (entry, selected) => h('strong', selected ? entry.value : 'option') };
  const node = render(option, true);
  assert.equal(node.type, 'span');
  assert.equal(node.children[0].type, 'strong');
  assert.equal(node.children[0].children, 'custom');
});

test('function labels suppress native titles while preserving option values and disabled states', () => {
  const { withSelectLabels, selectFallbackOption } = renderer();
  const options = [{ label: '启动程序', value: 'program', disabled: true }];
  const converted = withSelectLabels(options);
  assert.equal(typeof converted[0].label, 'function');
  assert.equal(converted[0].value, 'program');
  assert.equal(converted[0].disabled, true);
  assert.equal(converted[0].label(converted[0], true).children.default(), '启动程序');
  assert.equal(options[0].label, '启动程序');
  const fallback = selectFallbackOption('missing');
  assert.equal(fallback.value, 'missing');
  assert.equal(fallback.label(fallback, true).children.default(), 'missing');
});

test('every host select shares function labels and fallback labels', () => {
  for (const path of [
    'components/AppSelect.vue', 'components/AppMultiSelect.vue', 'components/RefreshToolbar.vue',
    'features/programs/ProgramsView.vue',
  ]) {
    const source = readFileSync(new URL(`../../src/${path}`, import.meta.url), 'utf8');
    const controls = [...source.matchAll(/<NSelect\b[^>]*>/g)];
    assert.ok(controls.length > 0, path);
    for (const [tag] of controls) {
      assert.ok(tag.includes(':fallback-option="selectFallbackOption"'), path);
      assert.ok(tag.includes(':options="withSelectLabels('), path);
    }
  }
});
