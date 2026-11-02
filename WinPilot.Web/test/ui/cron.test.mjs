import assert from 'node:assert/strict';
import { modalStateModule } from '../support/modalState.mjs';
import test, { afterEach } from 'node:test';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { loadScriptModule } from '../support/scriptModule.mjs';
import * as vue from 'vue';
import { elements, attribute, binding } from '../support/template.mjs';
import { readApiError } from '../support/apiErrors.mjs';

const componentUrl = new URL('../../src/features/tasks/CronEditor.vue', import.meta.url);
const componentSource = readFileSync(componentUrl, 'utf8');

test('generator fields use evenly distributed library underline tabs without custom selected styling', () => {
  assert.match(componentSource, /<NTabs\s+v-model:value="activeField"\s+type="line"\s+justify-content="space-evenly"[\s\S]*?aria-label="Cron 字段"\s*>\s*<NTab[\s\S]*?:name="index"/);
  assert.match(componentSource, /:tab-style="\{ width: '100%', justifyContent: 'center', minHeight: '44px' \}"/);
  assert.match(componentSource, /\.cron-generator-tabs :deep\(\.n-tabs-tab-wrapper\)\s*\{\s*flex: 1;\s*min-width: 0;/);
  assert.doesNotMatch(componentSource, /NRadioButton|accentTokens|:aria-pressed="activeField|@click="activeField = index"|\.cron-generator-tabs\s*\{/);
});
const componentScript = loadScriptModule(componentUrl,
  'generatorOpenModal,generatorOpen,activeField,rules,generated,generatorError,openGenerator,updateExpression,updateDraftExpression,editExpression,applyExpression,nextRuns,previewError,previewLoading,showValidation,invalidControl,rulesElement,expressionInput,expressionValid,previewValidationMessage,specialSyntaxHelp,choices,weekdays');
const signalScript = loadScriptModule(new URL('../../src/api/requestSignal.ts', import.meta.url));
const executorScript = loadScriptModule(new URL('../../src/api/requestExecutor.ts', import.meta.url));
const focusScript = loadScriptModule(new URL('../../src/ui/focusInvalidInput.ts', import.meta.url));
const disposals = new Set();
afterEach(() => {
  for (const dispose of disposals) dispose();
  disposals.clear();
});

function fixture(expression, { reducedMotion = false } = {}) {
  const emissions = [];
  const timers = new Map();
  const requests = [];
  let timerId = 0;
  let unmount;
  const focusModule = { exports: {}, window: { matchMedia: () => ({ matches: reducedMotion }) } };
  runInNewContext(focusScript, focusModule);
  const signalModule = { exports: {} };
  runInNewContext(signalScript, { ...signalModule, AbortController });
  const executorModule = { exports: {} };
  const client = {
    readApiError, isAbortError: error => error?.name === 'AbortError',
    apiFetch: (url, options) => new Promise((resolve, reject) => requests.push({ url, options, resolve, reject })),
  };
  runInNewContext(executorScript, {
    ...executorModule, DOMException,
    require: name => name === './client' ? client : signalModule.exports,
  });
  const props = vue.reactive({ modelValue: expression, error: '' });
  const context = {
    exports: {}, defineProps: () => props,
    defineEmits: () => (event, value) => {
      if (event === 'update:error') props.error = value;
      else { emissions.push(value); props.modelValue = value; }
    },
    AbortController, Error, encodeURIComponent,
    window: { matchMedia: () => ({ matches: reducedMotion }) },
    setTimeout: callback => { timers.set(++timerId, callback); return timerId; },
    clearTimeout: id => timers.delete(id),
    require: name => name.endsWith('/useModalState') ? modalStateModule : name === 'vue' ? { ...vue, useId: () => 'cron-test', onBeforeUnmount: callback => { unmount = callback; } }
      : name.endsWith('/theme') ? { useTheme: () => ({ accentTokens: vue.ref({ text: '#0067c0' }) }) } : name.endsWith('/client') ? client : name.endsWith('/requestExecutor') ? executorModule.exports
      : name.endsWith('/focusInvalidInput') ? focusModule.exports : {},
  };
  const scope = vue.effectScope();
  scope.run(() => runInNewContext(componentScript, context));
  let disposed = false;
  const dispose = () => {
    if (disposed) return;
    disposed = true;
    try { unmount(); }
    finally { scope.stop(); }
  };
  disposals.add(dispose);
  const flushPreview = () => { const callbacks = [...timers.values()]; timers.clear(); return Promise.all(callbacks.map(callback => callback())); };
  return { subject: context.exports, emissions, props, timers, requests,
    flushPreview,
    async completePreview(body = { nextRuns: [] }, status = 200) {
      const pending = flushPreview();
      requests.at(-1).resolve(Response.json(body, { status }));
      await pending;
    },
    unmount: dispose,
  };
}

test('both expression inputs accept arbitrary text without filtering typing or pasted content', () => {
  for (const inGenerator of [false, true]) {
    const f = fixture('0 0 8 * * *');
    if (inGenerator) f.subject.openGenerator();
    for (const value of ['0 30 9 L JAN MON-FRI', '中文😀', '０：３０', '$time!', '0 0 8 * * *备注', '']) {
      const input = { value };
      f.subject.editExpression({ target: input, inputType: 'insertFromPaste', data: null }, inGenerator);
      assert.equal(input.value, value);
      assert.equal(inGenerator ? f.subject.generated.value : f.props.modelValue, value);
    }
    if (inGenerator) assert.equal(f.props.modelValue, '0 0 8 * * *');
  }
});

test('both inputs wait for composition to finish without rejecting the committed text', () => {
  for (const inGenerator of [false, true]) {
    const f = fixture('0 0 8 * * *');
    if (inGenerator) f.subject.openGenerator();
    const input = { value: '中文' };
    f.subject.editExpression({ target: input, isComposing: true }, inGenerator);
    assert.equal(inGenerator ? f.subject.generated.value : f.props.modelValue, '0 0 8 * * *');
    f.subject.editExpression({ target: input }, inGenerator);
    assert.equal(inGenerator ? f.subject.generated.value : f.props.modelValue, '中文');
  }
});

test('save error belongs to the expression field and shares its standard spacing', () => {
  const field = elements(componentSource, node => node.tag === 'NFormItem' && attribute(node, 'label') === 'Cron 表达式')[0];
  assert.equal(binding(field, 'show-feedback'), 'Boolean(error)');
  assert.equal(binding(field, 'validation-status'), "error ? 'error' : undefined");
  assert.match(field.loc.source, /#feedback><span role="alert">{{ error }}<\/span>/);
});

test('expression inputs share edit handlers without character restriction listeners', () => {
  assert.doesNotMatch(componentSource, /@beforeinput|@paste|isCronText|blockInvalidCron/);
  assert.ok(componentSource.includes('@update:value="updateExpression"'));
  assert.ok(componentSource.includes('@update:value="editExpression($event, true)"'));
});

test('generator button stays inside the input suffix with the password icon button treatment', () => {
  const mainField = componentSource.split('<template>')[1].split('<AppModal')[0];
  assert.doesNotMatch(mainField, /NInputGroup/);
  assert.match(mainField, /<NInput\b[\s\S]*?<template #suffix>[\s\S]*?<NButton\s+quaternary\s+circle\s+size="tiny"\s+attr-type="button"[\s\S]*?aria-label="生成 Cron 表达式"\s+@mousedown\.prevent\s+@click="openGenerator"[\s\S]*?<AppIcon name="clock"\s*\/>[\s\S]*?<\/template>\s*<\/NInput>/);
});

test('unrestricted manual input is still checked by preview and cannot confirm invalid syntax', async () => {
  const f = fixture('0 0 8 * * *');
  f.subject.openGenerator();
  f.subject.editExpression({ target: { value: '中文😀' } }, true);
  assert.equal(f.subject.generated.value, '中文😀');
  const pending = f.flushPreview();
  assert.equal(new URL(f.requests[0].url, 'http://localhost').searchParams.get('expression'), '中文😀');
  f.requests[0].resolve(Response.json({ detail: 'Cron 表达式格式无效' }, { status: 400 }));
  await pending;
  await f.subject.applyExpression();
  assert.equal(f.subject.previewError.value, 'Cron 表达式格式无效');
  assert.equal(f.subject.generatorOpenModal.show, true);
  assert.deepEqual(f.emissions, []);
});

test('preview is debounced, uses the current expression and displays server times without changing the input', async () => {
  const f = fixture('0 0 8 * * *');
  assert.equal(f.timers.size, 0);
  f.subject.openGenerator();
  f.subject.rules.value[2].values = [9];
  f.subject.rules.value[2].values = [10];
  assert.equal(f.timers.size, 1);
  assert.equal(f.requests.length, 0);
  const pending = f.flushPreview();
  assert.equal(new URL(f.requests[0].url, 'http://localhost').searchParams.get('expression'), '0 0 10 * * *');
  const times = Array.from({ length: 5 }, (_, i) => `2026-10-${19 + i}T10:00:00+08:00`);
  f.requests[0].resolve(Response.json({ nextRuns: times }));
  await pending;
  assert.deepEqual(Array.from(f.subject.nextRuns.value), times);
  assert.equal(f.subject.previewLoading.value, false);
  assert.deepEqual(f.emissions, []);
});

test('editing cancels an in-flight response body and late results cannot replace the new preview', async () => {
  const f = fixture('0 0 8 * * *');
  f.subject.openGenerator();
  const first = f.flushPreview();
  let finishBody;
  let readingBody;
  const bodyStarted = new Promise(resolve => { readingBody = resolve; });
  f.requests[0].resolve({ ok: true, json: () => { readingBody(); return new Promise(resolve => { finishBody = resolve; }); } });
  await bodyStarted;
  f.subject.rules.value[2].values = [9];
  assert.equal(f.requests[0].options.signal.aborted, true);
  const second = f.flushPreview();
  f.requests[1].resolve(Response.json({ nextRuns: ['2026-10-19T09:00:00+08:00'] }));
  await second;
  finishBody({ nextRuns: ['old'] });
  await first;
  assert.deepEqual(Array.from(f.subject.nextRuns.value), ['2026-10-19T09:00:00+08:00']);
  assert.equal(f.subject.previewError.value, '');
});

test('preview distinguishes malformed syntax, no next occurrence and network errors from save errors', async () => {
  const f = fixture('0 0 8 * * BAD');
  f.props.error = '保存错误';
  f.subject.openGenerator();
  let pending = f.flushPreview();
  f.requests[0].resolve(Response.json({ detail: 'Cron 表达式格式无效' }, { status: 400 }));
  await pending;
  assert.equal(f.subject.previewError.value, 'Cron 表达式格式无效');
  f.subject.rules.value[5].mode = 'every';
  pending = f.flushPreview();
  f.requests[1].resolve(Response.json({ nextRuns: [] }));
  await pending;
  assert.equal(f.subject.previewError.value, '');
  assert.equal(f.subject.nextRuns.value.length, 0);
  f.subject.rules.value[2].values = [9];
  pending = f.flushPreview();
  f.requests[2].reject(new Error('无法连接到 WinPilot'));
  await pending;
  assert.equal(f.subject.previewError.value, '无法连接到 WinPilot');
  assert.equal(f.props.error, '保存错误');
});

test('invalid selections skip requests and closing or unmounting cancels pending work', async () => {
  for (const close of [f => { f.subject.generatorOpenModal.close(); }, f => f.unmount()]) {
    const f = fixture('0 0 8 * * *');
    f.subject.openGenerator();
    f.subject.rules.value[1].values = [];
    assert.equal(f.timers.size, 0);
    f.subject.rules.value[1].values = [0];
    const pending = f.flushPreview();
    close(f);
    assert.equal(f.requests[0].options.signal.aborted, true);
    f.requests[0].resolve(Response.json({ nextRuns: ['late'] }));
    await pending;
    assert.equal(f.subject.nextRuns.value.length, 0);
    assert.equal(f.subject.previewError.value, '');
    assert.equal(f.timers.size, 0);
  }
});

test('closing or unmounting during debounce prevents requests and repeated cleanup is safe', async () => {
  for (const close of [f => { f.subject.generatorOpenModal.close(); }, f => f.unmount()]) {
    const f = fixture('0 0 8 * * *');
    f.subject.openGenerator();
    assert.equal(f.timers.size, 1);
    close(f);
    await f.flushPreview();
    assert.equal(f.requests.length, 0);
    assert.equal(f.timers.size, 0);
    f.unmount();
    f.unmount();
  }
});

test('a late aborted request failure cannot replace the current preview or clear its loading state', async () => {
  const f = fixture('0 0 8 * * *');
  f.subject.openGenerator();
  const first = f.flushPreview();
  f.subject.rules.value[2].values = [9];
  const second = f.flushPreview();
  f.requests[0].reject(new Error('旧请求失败'));
  await first;
  assert.equal(f.subject.previewError.value, '');
  assert.equal(f.subject.previewLoading.value, true);
  f.requests[1].resolve(Response.json({ nextRuns: ['2026-10-19T09:00:00+08:00'] }));
  await second;
  assert.equal(f.subject.previewLoading.value, false);
  assert.deepEqual(Array.from(f.subject.nextRuns.value), ['2026-10-19T09:00:00+08:00']);
});

test('opening and cancelling the generator never changes the expression or save error', () => {
  const f = fixture('0 30 */2 * * 0');
  f.props.error = '保存校验错误';
  assert.deepEqual(f.emissions, []);
  f.subject.openGenerator();
  assert.equal(f.subject.generatorOpenModal.show, true);
  f.subject.rules.value[1].mode = 'every';
  f.subject.generatorOpenModal.close();
  assert.equal(f.props.modelValue, '0 30 */2 * * 0');
  assert.equal(f.props.error, '保存校验错误');
  assert.deepEqual(f.emissions, []);
});

test('generator restores interval, range and multiple values across six fields', async () => {
  const f = fixture('0 */5 8-18 1,15 * 1-5');
  f.subject.openGenerator();
  assert.equal(f.subject.generated.value, '0 */5 8-18 1,15 * 1-5');
  assert.equal(f.subject.generatorError.value, '');
  f.subject.rules.value[4].mode = 'values';
  f.subject.rules.value[4].values = [12, 1, 6];
  f.subject.rules.value[5].mode = 'values';
  f.subject.rules.value[5].values = [6, 0];
  assert.equal(f.subject.generated.value, '0 0/5 8-18 1,15 1,6,12 0,6');
  f.props.error = '保存校验错误';
  await f.completePreview();
  f.subject.applyExpression();
  assert.deepEqual(f.emissions, ['0 0/5 8-18 1,15 1,6,12 0,6']);
  assert.equal(f.props.error, '');
  assert.equal(f.subject.generatorOpenModal.show, false);
});

test('reopening discards cancelled drafts and reads the current expression', () => {
  const f = fixture('0 0 8 * * *');
  f.subject.openGenerator();
  f.subject.rules.value[2].values = [9];
  f.subject.generatorOpenModal.close();
  f.subject.openGenerator();
  assert.equal(f.subject.generated.value, '0 0 8 * * *');
  f.props.modelValue = '0 15 10 * * 6';
  f.subject.openGenerator();
  assert.equal(f.subject.generated.value, '0 15 10 * * 6');
});

test('special Cron syntax is preserved until that field is explicitly replaced', () => {
  const f = fixture('0 0 8 L * MON-FRI');
  f.subject.openGenerator();
  assert.equal(f.subject.generated.value, '0 0 8 L * MON-FRI');
  f.subject.rules.value[3].mode = 'values';
  f.subject.rules.value[3].values = [1];
  assert.equal(f.subject.generated.value, '0 0 8 1 * MON-FRI');
});

test('malformed input is preserved and format errors are displayed by preview instead of resetting the draft', async () => {
  for (const expression of ['invalid', '30 8 * * *', '0 0 8 * * * 2030', 'invalid 0 8 * * *']) {
    const f = fixture(expression);
    f.props.error = '保存校验错误';
    f.subject.openGenerator();
    assert.equal(f.subject.generatorOpenModal.show, true);
    assert.equal(f.subject.generated.value, expression);
    const pending = f.flushPreview();
    assert.equal(new URL(f.requests[0].url, 'http://localhost').searchParams.get('expression'), expression);
    f.requests[0].resolve(Response.json({ detail: 'Cron 表达式格式无效' }, { status: 400 }));
    await pending;
    assert.equal(f.subject.previewError.value, 'Cron 表达式格式无效');
    assert.equal(f.props.error, '保存校验错误');
    assert.equal(f.props.modelValue, expression);
    assert.deepEqual(f.emissions, []);
  }
});

test('manual draft edits preview immediately after debounce and only update the form on confirmation', async () => {
  const f = fixture('0 0 8 * * *');
  f.props.error = '保存校验错误';
  f.subject.openGenerator();
  f.subject.updateDraftExpression('0 30 9 L * *');
  assert.equal(f.subject.generated.value, '0 30 9 L * *');
  assert.equal(f.props.modelValue, '0 0 8 * * *');
  assert.equal(f.props.error, '保存校验错误');
  assert.deepEqual(f.emissions, []);
  const pending = f.flushPreview();
  assert.equal(new URL(f.requests[0].url, 'http://localhost').searchParams.get('expression'), '0 30 9 L * *');
  f.requests[0].resolve(Response.json({ nextRuns: ['2030-01-31T09:30:00+08:00'] }));
  await pending;
  assert.equal(f.subject.nextRuns.value.length, 1);
  f.subject.applyExpression();
  assert.deepEqual(f.emissions, ['0 30 9 L * *']);
});

test('invalid or cleared manual drafts are not replaced by defaults and show preview errors', async () => {
  for (const expression of ['invalid', '']) {
    const f = fixture('0 0 8 * * *');
    f.subject.openGenerator();
    f.subject.updateDraftExpression(expression);
    assert.equal(f.subject.generated.value, expression);
    const pending = f.flushPreview();
    assert.equal(new URL(f.requests[0].url, 'http://localhost').searchParams.get('expression'), expression);
    const message = expression ? 'Cron 表达式格式无效' : '请输入 Cron 表达式';
    f.requests[0].resolve(Response.json({ detail: message }, { status: 400 }));
    await pending;
    assert.equal(f.subject.previewError.value, message);
    assert.equal(f.props.modelValue, '0 0 8 * * *');
    f.subject.generatorOpenModal.close();
    assert.deepEqual(f.emissions, []);
  }
});

test('confirmation waits for the current preview, including debounce and edits after successful validation', async () => {
  const f = fixture('0 0 8 * * *');
  f.subject.openGenerator();
  await f.subject.applyExpression();
  assert.deepEqual(f.emissions, []);
  const pending = f.flushPreview();
  await f.subject.applyExpression();
  assert.deepEqual(f.emissions, []);
  f.requests[0].resolve(Response.json({ nextRuns: [] }));
  await pending;
  assert.equal(f.subject.expressionValid.value, true);
  f.subject.updateDraftExpression('0 30 9 * * *');
  assert.equal(f.subject.expressionValid.value, undefined);
  await f.subject.applyExpression();
  assert.deepEqual(f.emissions, []);
  await f.completePreview();
  await f.subject.applyExpression();
  assert.deepEqual(f.emissions, ['0 30 9 * * *']);
});

test('invalid and empty manual expressions focus and shake only on confirmation and edits clear validation', async () => {
  for (const expression of ['invalid', '']) {
    const f = fixture('0 0 8 * * *');
    f.subject.openGenerator();
    f.subject.updateDraftExpression(expression);
    const calls = [];
    f.subject.expressionInput.value = { closest: () => null, scrollIntoView() {}, focus: () => calls.push('focus'), animate: () => calls.push('animate') };
    await f.completePreview({ detail: 'Cron 表达式格式无效' }, 400);
    assert.equal(f.subject.showValidation.value, false);
    assert.deepEqual(calls, []);
    await f.subject.applyExpression();
    assert.equal(f.subject.generatorOpenModal.show, true);
    assert.equal(f.subject.showValidation.value, true);
    assert.deepEqual(calls, ['focus', 'animate']);
    assert.deepEqual(f.emissions, []);
    f.subject.updateDraftExpression('0 0 9 * * *');
    assert.equal(f.subject.showValidation.value, false);
    assert.equal(f.subject.expressionValid.value, undefined);
  }
});

test('manual syntax failures respect reduced motion', async () => {
  const f = fixture('invalid', { reducedMotion: true });
  f.subject.openGenerator();
  const calls = [];
  f.subject.expressionInput.value = { closest: () => null, scrollIntoView() {}, focus: () => calls.push('focus'), animate: () => calls.push('animate') };
  await f.completePreview({ detail: 'Cron 表达式格式无效' }, 400);
  await f.subject.applyExpression();
  assert.deepEqual(calls, ['focus']);
});

test('network and server failures prevent confirmation without blaming or shaking the input', async () => {
  for (const networkFailure of [false, true]) {
    const f = fixture('0 0 8 * * *');
    f.subject.openGenerator();
    const calls = [];
    f.subject.expressionInput.value = { closest: () => null, scrollIntoView() {}, focus: () => calls.push('focus'), animate: () => calls.push('animate') };
    const pending = f.flushPreview();
    if (networkFailure) f.requests[0].reject(new Error('无法连接到 WinPilot'));
    else f.requests[0].resolve(Response.json({ detail: '无法计算执行时间，请重试' }, { status: 500 }));
    await pending;
    await f.subject.applyExpression();
    assert.deepEqual(calls, []);
    assert.deepEqual(f.emissions, []);
    assert.equal(f.subject.generatorOpenModal.show, true);
    assert.equal(f.subject.expressionValid.value, undefined);
    assert.notEqual(f.subject.previewError.value, '');
  }
});

test('an obsolete syntax error cannot invalidate a newer successfully validated expression', async () => {
  const f = fixture('invalid');
  f.subject.openGenerator();
  const first = f.flushPreview();
  f.subject.updateDraftExpression('0 0 9 * * *');
  await f.completePreview();
  f.requests[0].resolve(Response.json({ detail: 'Cron 表达式格式无效' }, { status: 400 }));
  await first;
  assert.equal(f.subject.expressionValid.value, true);
  await f.subject.applyExpression();
  assert.deepEqual(f.emissions, ['0 0 9 * * *']);
});

test('an obsolete error body cannot publish validation state after a newer preview finishes', async () => {
  const f = fixture('invalid');
  f.subject.openGenerator();
  const first = f.flushPreview();
  let finishBody;
  let startBody;
  const bodyStarted = new Promise(resolve => { startBody = resolve; });
  f.requests[0].resolve({ ok: false, status: 400, json: () => {
    startBody();
    return new Promise(resolve => { finishBody = resolve; });
  } });
  await bodyStarted;
  assert.equal(f.subject.expressionValid.value, undefined);
  assert.equal(f.subject.previewError.value, '');
  assert.equal(f.subject.previewLoading.value, true);
  f.subject.updateDraftExpression('0 0 9 * * *');
  await f.completePreview({ nextRuns: ['2026-10-19T09:00:00+08:00'] });
  finishBody({ detail: 'Cron 表达式格式无效' });
  await first;
  assert.equal(f.subject.expressionValid.value, true);
  assert.equal(f.subject.previewError.value, '');
  assert.equal(f.subject.previewLoading.value, false);
  assert.deepEqual(Array.from(f.subject.nextRuns.value), ['2026-10-19T09:00:00+08:00']);
});

test('a response parsing failure stays a request failure rather than invalid syntax', async () => {
  const f = fixture('0 0 8 * * *');
  f.subject.openGenerator();
  const pending = f.flushPreview();
  f.requests[0].resolve({ ok: true, json: () => Promise.reject(new Error('响应解析失败')) });
  await pending;
  assert.equal(f.subject.expressionValid.value, undefined);
  assert.equal(f.subject.previewError.value, '响应解析失败');
  assert.equal(f.subject.previewLoading.value, false);
  await f.subject.applyExpression();
  assert.deepEqual(f.emissions, []);
});

test('a closed modal cannot confirm even after a successful preview', async () => {
  const f = fixture('0 0 8 * * *');
  f.subject.openGenerator();
  await f.completePreview();
  f.subject.generatorOpenModal.close();
  await f.subject.applyExpression();
  assert.deepEqual(f.emissions, []);
});

test('manual drafts supersede stale rule validation and changing a rule resumes generation', async () => {
  const f = fixture('0 0 8 * * *');
  f.subject.openGenerator();
  Object.assign(f.subject.rules.value[0], { mode: 'interval', start: '' });
  await f.subject.applyExpression();
  assert.notEqual(f.subject.generatorError.value, '');
  f.subject.updateDraftExpression('0 30 9 * * *');
  assert.equal(f.subject.generatorError.value, '');
  assert.equal(f.subject.showValidation.value, false);
  await f.completePreview();
  f.subject.applyExpression();
  assert.deepEqual(f.emissions, ['0 30 9 * * *']);
  f.subject.openGenerator();
  f.subject.updateDraftExpression('0 0 12 L * *');
  f.subject.rules.value[2].values = [10];
  assert.equal(f.subject.generated.value, '0 30 10 * * *');
});

test('only blank input initializes every field to the default rule', () => {
  for (const expression of ['', '  \t ']) {
    const f = fixture(expression);
    f.subject.openGenerator();
    assert.equal(f.subject.generated.value, '* * * * * *');
    assert.ok(f.subject.rules.value.every(rule => rule.mode === 'every'));
    assert.equal(f.props.modelValue, expression);
    assert.deepEqual(f.emissions, []);
  }
});

test('opening and confirming without editing preserves the original expression exactly', async () => {
  const expression = '  0  */5  8 * * *  ';
  const f = fixture(expression);
  f.subject.openGenerator();
  assert.equal(f.subject.generated.value, expression);
  await f.completePreview();
  f.subject.applyExpression();
  assert.deepEqual(f.emissions, [expression]);
});

test('editing a rule generates a new expression without changing the form until confirmation', async () => {
  for (const expression of ['0 0 8 * *', '0 0 8 * * * 2030']) {
    const f = fixture(expression);
    f.subject.openGenerator();
    assert.equal(f.subject.generated.value, expression);
    Object.assign(f.subject.rules.value[0], { mode: 'values', values: [0] });
    assert.equal(f.subject.generated.value, '0 * * * * *');
    assert.equal(f.props.modelValue, expression);
    assert.deepEqual(f.emissions, []);
    await f.completePreview();
    f.subject.applyExpression();
    assert.deepEqual(f.emissions, ['0 * * * * *']);
  }
});

test('invalid intervals, reversed ranges and empty selections cannot be applied', () => {
  for (const changes of [
    { mode: 'interval', step: 0 }, { mode: 'interval', step: 1.5 },
    { mode: 'interval', step: '' }, { mode: 'interval', start: 60 },
    { mode: 'range', start: 20, end: 10 }, { mode: 'range', end: 60 },
    { mode: 'values', values: [] }, { mode: 'values', values: [60] },
  ]) {
    const f = fixture('0 0 8 * * *');
    f.subject.openGenerator();
    Object.assign(f.subject.rules.value[1], changes);
    assert.notEqual(f.subject.generatorError.value, '');
    f.subject.applyExpression();
    assert.deepEqual(f.emissions, []);
    assert.equal(f.subject.generatorOpenModal.show, true);
  }
});

test('typing directly clears the save error without opening the generator', () => {
  const f = fixture('0 0 8 * * *');
  f.props.error = 'Cron 表达式格式无效';
  f.subject.updateExpression('0 0 9 * * *');
  assert.equal(f.props.error, '');
  assert.deepEqual(f.emissions, ['0 0 9 * * *']);
  assert.equal(f.subject.generatorOpenModal.show, false);
});

test('weekday ranges, intervals and Sunday selections use zero through six', async () => {
  for (const source of ['0-6', '5-6', '0/2', '6/1', '0,6', '0']) {
    const f = fixture(`0 0 8 * * ${source}`);
    f.subject.openGenerator();
    assert.notEqual(f.subject.rules.value[5].mode, 'custom');
    assert.equal(f.subject.generatorError.value, '');
    f.subject.activeField.value = 5;
    assert.deepEqual(Array.from(f.subject.choices.value), [0, 1, 2, 3, 4, 5, 6]);
    assert.equal(f.subject.weekdays[0], '周日');
    assert.equal(f.subject.weekdays[6], '周六');
    await f.completePreview();
    f.subject.applyExpression();
    assert.deepEqual(f.emissions, [`0 0 8 * * ${source}`]);
  }
});

test('weekday wildcard steps share the same zero-based interval parsing as other fields', async () => {
  const f = fixture('0 0 8 * * */2');
  f.subject.openGenerator();
  assert.equal(f.subject.rules.value[5].mode, 'interval');
  assert.equal(f.subject.rules.value[5].start, 0);
  assert.equal(f.subject.rules.value[5].step, 2);
  await f.completePreview();
  f.subject.applyExpression();
  assert.deepEqual(f.emissions, ['0 0 8 * * */2']);
});

test('weekday expressions containing seven remain unchanged as custom syntax', async () => {
  for (const source of ['7', '1-7', '6-7', '7/1', '1,7']) {
    const expression = `0 0 8 * * ${source}`;
    const f = fixture(expression);
    f.subject.openGenerator();
    assert.equal(f.subject.rules.value[5].mode, 'custom');
    await f.completePreview();
    f.subject.applyExpression();
    assert.deepEqual(f.emissions, [expression]);
  }
});

test('seconds rules support every, range, interval and specified values', async () => {
  for (const [changes, expected] of [
    [{ mode: 'every' }, '*'],
    [{ mode: 'range', start: 10, end: 20 }, '10-20'],
    [{ mode: 'interval', start: 0, step: 5 }, '0/5'],
    [{ mode: 'values', values: [59, 0, 30] }, '0,30,59'],
  ]) {
    const f = fixture('15 30 8 * * 1-7');
    f.subject.openGenerator();
    assert.equal(f.subject.rules.value.length, 6);
    assert.equal(f.subject.generated.value, '15 30 8 * * 1-7');
    Object.assign(f.subject.rules.value[0], changes);
    await f.completePreview();
    f.subject.applyExpression();
    assert.deepEqual(f.emissions, [`${expected} 30 8 * * 1-7`]);
  }
});

test('seconds reject out-of-range input and five-field input is not automatically converted', () => {
  const f = fixture('30 8 * * *');
  f.subject.openGenerator();
  assert.equal(f.props.modelValue, '30 8 * * *');
  assert.equal(f.subject.generated.value, '30 8 * * *');
  assert.deepEqual(f.emissions, []);
  Object.assign(f.subject.rules.value[0], { mode: 'values', values: [60] });
  f.subject.applyExpression();
  assert.notEqual(f.subject.generatorError.value, '');
  assert.deepEqual(f.emissions, []);
});

test('temporary empty values remain neutral until confirmation and edits clear validation', async () => {
  const f = fixture('0/5 * * * * *');
  f.subject.openGenerator();
  f.subject.rules.value[0].start = '';
  assert.notEqual(f.subject.generatorError.value, '');
  assert.equal(f.subject.showValidation.value, false);
  assert.equal(f.subject.invalidControl.value, undefined);
  assert.equal(f.timers.size, 0);
  await f.subject.applyExpression();
  assert.equal(f.subject.showValidation.value, true);
  assert.equal(f.subject.invalidControl.value, 'start');
  assert.equal(f.subject.generatorOpenModal.show, true);
  assert.deepEqual(f.emissions, []);
  f.subject.rules.value[0].start = 60;
  assert.equal(f.subject.showValidation.value, false);
  assert.notEqual(f.subject.generatorError.value, '');
  f.subject.rules.value[0].start = 0;
  assert.equal(f.subject.generatorError.value, '');
  assert.equal(f.timers.size, 1);
  f.subject.generatorOpenModal.close();
  f.subject.openGenerator();
  assert.equal(f.subject.showValidation.value, false);
});

test('confirmation selects the first invalid tab and focuses and animates only its invalid input', async () => {
  for (const [changes, control] of [
    [{ mode: 'interval', start: '', step: 5 }, 'start'],
    [{ mode: 'interval', start: 0, step: '' }, 'step'],
    [{ mode: 'range', start: 0, end: 60 }, 'end'],
    [{ mode: 'range', start: 20, end: 10 }, 'start'],
    [{ mode: 'values', values: [] }, 'values'],
  ]) {
    const f = fixture('0 0 8 * * *');
    f.subject.openGenerator();
    Object.assign(f.subject.rules.value[1], changes);
    f.subject.activeField.value = 5;
    const calls = [];
    f.subject.rulesElement.value = { querySelector: selector => {
      calls.push(selector);
      return { closest: () => null, scrollIntoView() {}, focus: () => calls.push('focus'), animate: () => calls.push('animate') };
    } };
    await f.subject.applyExpression();
    assert.equal(f.subject.activeField.value, 1);
    assert.equal(f.subject.invalidControl.value, control);
    assert.deepEqual(calls, [`[data-cron-mode="${changes.mode}"] [data-control="${control}"] :is(input, [role="checkbox"])`, 'focus', 'animate']);
    assert.deepEqual(f.emissions, []);
  }
});

test('reduced motion keeps focus without animation and closing prevents late focus', async () => {
  for (const close of [false, true]) {
    const f = fixture('0/5 * * * * *', { reducedMotion: true });
    f.subject.openGenerator();
    f.subject.rules.value[0].step = 0;
    const calls = [];
    f.subject.rulesElement.value = { querySelector: () => ({
      closest: () => null, scrollIntoView() {},
      focus: () => calls.push('focus'), animate: () => calls.push('animate'),
    }) };
    const confirming = f.subject.applyExpression();
    if (close) f.subject.generatorOpenModal.close();
    await confirming;
    assert.deepEqual(calls, close ? [] : ['focus']);
  }
});

test('confirmation errors stay beside their controls while preview keeps status and request errors separate', () => {
  const source = componentSource;
  const controls = elements(source, node => node.tag === 'NFormItem' && ['start', 'end', 'step'].includes(attribute(node, 'data-control')));
  assert.equal(controls.length, 4);
  for (const field of controls) {
    const control = attribute(field, 'data-control');
    assert.ok(field.loc.source.indexOf('<AppNumberInput') < field.loc.source.indexOf('#feedback'));
    assert.equal(binding(field, 'show-feedback'), `Boolean(invalidControl === '${control}')`);
    assert.match(field.loc.source, /{{\s*generatorError\s*}}/);
  }
  assert.ok(elements(source, node => node.tag === 'NFormItem').some(field => binding(field, 'show-feedback') === "invalidControl === 'values'" && field.loc.source.includes('<template #feedback><span role="alert">{{ generatorError }}</span></template>')));
  const preview = source.split('<div class="cron-preview-body">')[1].split('</div>')[0];
  assert.match(preview, /{{\s*previewValidationMessage\s*}}/);
  assert.ok(!preview.includes('{{ generatorError }}'));
  assert.match(preview, /{{\s*previewError\s*}}/);
  assert.doesNotMatch(source, /\.cron-generator \.field-error\s*\{/);
});

test('preview distinguishes missing values from invalid values without displaying field errors before confirmation', () => {
  for (const [changes, message] of [
    [{ mode: 'interval', start: '', step: 1 }, '表达式不完整，无法预览'],
    [{ mode: 'interval', start: 0, step: '' }, '表达式不完整，无法预览'],
    [{ mode: 'range', start: 0, end: '' }, '表达式不完整，无法预览'],
    [{ mode: 'values', values: [] }, '表达式不完整，无法预览'],
    [{ mode: 'interval', start: 60, step: 1 }, '表达式无效，无法预览'],
    [{ mode: 'interval', start: 0, step: 0 }, '表达式无效，无法预览'],
    [{ mode: 'range', start: 20, end: 10 }, '表达式无效，无法预览'],
    [{ mode: 'values', values: [60] }, '表达式无效，无法预览'],
  ]) {
    const f = fixture('0 0 8 * * *');
    f.subject.openGenerator();
    Object.assign(f.subject.rules.value[0], changes);
    assert.equal(f.subject.previewValidationMessage.value, message);
    assert.equal(f.subject.showValidation.value, false);
    assert.equal(f.timers.size, 0);
    f.subject.rules.value[0].mode = 'every';
    assert.equal(f.subject.previewValidationMessage.value, '');
    assert.equal(f.timers.size, 1);
  }
});

test('generator expression reuses the host help hint with compact special syntax and manual editing instructions', () => {
  const f = fixture('0 30 8 L * *');
  const help = f.subject.specialSyntaxHelp;
  for (const text of ['L-1', '15W', 'LW', '1#2', '5L', '10-30/5', '不考虑法定节假日', '下方直接编辑', '保留原值'])
    assert.ok(help.includes(text));
  assert.ok(!help.includes('\n\n'));
  assert.ok(help.startsWith('特殊语法\n日：'));
  assert.ok(help.includes('\n工作日指周一至周五'));
  assert.ok(!help.includes('高级语法'));
  const source = componentSource;
  assert.ok(source.includes('<HelpHint :label="specialSyntaxHelp" />'));
  assert.ok(elements(source, node => node.tag === 'NFormItem').some(field => binding(field, 'label-props') === '{ for: expressionId }' && field.loc.source.includes('<span>Cron 表达式</span>')));
  assert.match(source, /<NInput[\s\S]*?:value="generated"[\s\S]*?id: expressionId[\s\S]*?@update:value="editExpression\(\$event, true\)"/);
  assert.ok(source.includes('?.inputElRef ?? undefined'));
  assert.doesNotMatch(source, /readonly/);
  f.subject.openGenerator();
  assert.equal(f.subject.generated.value, '0 30 8 L * *');
  assert.deepEqual(f.emissions, []);
});

test('parsing preserves unsupported syntax and deduplicates numeric selections without changing other fields', () => {
  for (const raw of ['60', '10-5', '0/0', '0/61', '10-30/5']) {
    const expression = `${raw} 0 8 L * MON-FRI`;
    const f = fixture(expression);
    f.subject.openGenerator();
    assert.equal(f.subject.rules.value[0].mode, 'custom');
    assert.equal(f.subject.generated.value, expression);
  }
  const f = fixture('30,0,30 0 8 L * MON-FRI');
  f.subject.openGenerator();
  assert.deepEqual(Array.from(f.subject.rules.value[0].values), [30, 0]);
  assert.equal(f.subject.generated.value, '30,0,30 0 8 L * MON-FRI');
  f.subject.rules.value[0].values.push(59);
  assert.equal(f.subject.generated.value, '0,30,59 0 8 L * MON-FRI');
  assert.deepEqual(Array.from(f.subject.rules.value[1].values), [0]);
});
test('specified Cron values fill the form width for a responsive multi-column grid', () => {
  const source = readFileSync(new URL('../../src/features/tasks/CronEditor.vue', import.meta.url), 'utf8');
  assert.match(source, /\.cron-generator-values\s*\{[^}]*width: 100%;[^}]*grid-template-columns: repeat\(auto-fit, minmax\(64px, 1fr\)\)/);
});
