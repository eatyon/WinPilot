import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';

const models = { exports: {} };
runInNewContext(loadScriptModule(new URL('../../src/features/plugins/pluginModels.ts', import.meta.url)), models);
const context = { exports: {}, require: () => models.exports };
runInNewContext(loadScriptModule(new URL('../../src/features/plugins/pluginConfiguration.ts', import.meta.url)), context);
const { configurationError } = context.exports;
const collection = {
  name: 'profiles', type: 'collection', mode: 'dynamic',
  item: { fields: [{ name: 'target', label: '目标', type: 'text', required: true }] },
};

test('plugin configuration validates top-level required fields before collections', () => {
  const definitions = [collection, { name: 'token', label: '令牌', type: 'text', required: true }];
  assert.equal(configurationError(definitions, { profiles: [{}] }), '令牌不能为空');
  assert.equal(configurationError(definitions, { token: '' }), '令牌不能为空');
  assert.equal(configurationError(definitions, { token: 'valid', profiles: [] }), '');
});

test('plugin configuration preserves optional, zero, false and numeric field values', () => {
  const definitions = [
    { name: 'optional', type: 'text' },
    { name: 'count', type: 'number', required: true },
    { name: 'enabled', type: 'checkbox', required: true },
  ];
  const config = { optional: '', count: 0, enabled: false };
  assert.equal(configurationError(definitions, config), '');
  assert.deepEqual(config, { optional: '', count: 0, enabled: false });
  assert.equal(configurationError(definitions, { enabled: false }), '');
});

test('dynamic collection remarks precede child field validation and preserve item order', () => {
  const first = { __winpilot: { id: 'first', remark: '配置一' }, target: '' };
  const second = { __winpilot: { id: 'second', remark: '  ' }, target: 'valid' };
  assert.equal(configurationError([collection], { profiles: [first, second] }), '备注不能为空');
  second.__winpilot.remark = '配置二';
  assert.equal(configurationError([collection], { profiles: [first, second] }), '目标不能为空');
  for (const value of [undefined, null, '', []]) {
    assert.equal(configurationError([collection], { profiles: [{ ...first, target: value }] }), '目标不能为空');
  }
  assert.equal(configurationError([collection], { profiles: [{ ...first, target: 'valid' }] }), '');
});

test('fixed and absent collections retain their current validation scope', () => {
  assert.equal(configurationError([collection], {}), '');
  assert.equal(configurationError([{ ...collection, mode: 'fixed' }], { profiles: [{}] }), '');
});
