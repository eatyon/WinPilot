import assert from 'node:assert/strict';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import { readFileSync } from 'node:fs';
import * as vue from 'vue';
import * as apiErrors from '../support/apiErrors.mjs';

test('mobile query controls use a full-width user field and one button row without desktop overrides', () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  assert.ok(source.includes('class="program-actions process-query-actions"'));
  assert.ok(source.includes("isQuerying ? '查询中…' : '查询'"));
  const mobile = source.match(/@media \(max-width: 767px\)\s*\{([\s\S]*?)\n\}/)?.[1];
  assert.match(mobile, /\.process-query-actions \.user-select\s*\{[^}]*grid-template-columns:\s*minmax\(0, 1fr\);[^}]*flex:\s*0 0 100%/);
  assert.match(mobile, /\.process-query-actions \.button\s*\{[^}]*min-width:\s*0;[^}]*white-space:\s*nowrap/);
  assert.equal(source.replace(mobile, '').includes('.process-query-actions .'), false);
  const css = readFileSync(new URL('../../src/style.css', import.meta.url), 'utf8');
  assert.match(css, /\.program-actions\s*\{[^}]*display:\s*flex;[^}]*flex-wrap:\s*wrap/);
  assert.match(css, /\.program-panel-header \.program-actions \.button\s*\{\s*flex:\s*1/);
});

test('tag paths appear beneath their names only in management mode', () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  assert.match(source, /<div class="tag-info">\s*<strong>\{\{ tag.name \}\}<\/strong>\s*<small v-if="managingTags && !safeModeEnabled" :title="tag.programPath">\{\{ tag.programPath \}\}<\/small>\s*<\/div>/);
  const css = readFileSync(new URL('../../src/style.css', import.meta.url), 'utf8');
  assert.match(css, /\.tag-row\s*\{[^}]*grid-template-columns:\s*minmax\(0, 1fr\) auto/);
  assert.match(css, /\.tag-info\s*\{[^}]*display:\s*grid;[^}]*min-width:\s*0/);
  assert.match(css, /\.tag-row small\s*\{[^}]*text-overflow:\s*ellipsis;[^}]*white-space:\s*nowrap/);
  assert.match(css, /\.tag-row strong\s*\{[^}]*font-size:\s*16px/);
  assert.match(source, /\.tag-controls\s*\{[^}]*width:\s*min\(100%, 320px\)/);
  const mobile = source.match(/@media \(max-width: 767px\)\s*\{([\s\S]*?)\n\}/)?.[1];
  assert.match(mobile, /\.tag-info small\s*\{[^}]*white-space:\s*normal;[^}]*overflow-wrap:\s*anywhere/);
  assert.match(mobile, /\.tag-controls\s*\{\s*width:\s*100%/);
});

test('process search appears with query results and uses the tag search font', () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  assert.match(source, /<template v-if="hasQueried">\s*<div class="program-query-bar">/);
  assert.match(source, /<input v-model="programSearch"[^>]*aria-label="搜索进程名"/);
  assert.equal(source.includes(':disabled="!hasQueried"'), false);
  const css = readFileSync(new URL('../../src/style.css', import.meta.url), 'utf8');
  assert.match(css, /\.program-search input\s*\{[^}]*font-size:\s*14px;[^}]*font-weight:\s*400/);
  assert.match(css, /\.tag-search input\s*\{[^}]*font-size:\s*14px/);
});

test('mobile program pagination follows the compact file pagination layout', () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  const mobile = source.match(/@media \(max-width: 767px\)\s*\{([\s\S]*?)\n\}/)?.[1];
  assert.match(mobile, /\.programs-view \.program-pagination\s*\{[^}]*flex-direction:\s*row/);
  assert.match(mobile, /\.programs-view \.page-size-control\s*\{[^}]*grid-template-columns:\s*auto 64px/);
  assert.match(mobile, /\.programs-view \.pagination-actions\s*\{[^}]*display:\s*flex/);
  assert.match(mobile, /\.programs-view \.pagination-actions \.button\s*\{[^}]*width:\s*auto;[^}]*min-width:\s*68px/);
});

async function fixture() {
  const code = loadScriptModule(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url),
    'launch, saveTag, launchTag, queryPrograms, collapsePrograms, showUsage, refreshUsage, closeUsage, processUsage, processUsageTarget, isUsageLoading, isQuerying, hasQueried, programs, groups, selectedUserName, tagName, pendingActions, launchFormError, pageError, editTag, cancelTagEdit, editingTagName, launchPanel, programPath, argumentsText, workingDirectory, runElevated, silent, launchFormMessage, managingTags, toggleTagManagement, tagDeleteTarget');
  let mounted;
  let unmount;
  const requests = [];
  const toasts = [];
  const safeModeEnabled = vue.ref(false);
  const context = {
    exports: {}, AbortController,
    require: name => {
      if (name === 'vue') return { ...vue, onMounted: callback => { mounted = callback; }, onBeforeUnmount: callback => { unmount = callback; } };
      if (name.endsWith('/client')) return { ...apiErrors,
        apiFetch: (url, init) => new Promise((resolve, reject) => requests.push({ url, init, resolve, reject })),
        isAbortError: error => error?.name === 'AbortError', userErrorMessage: error => error.message,
      };
      if (name.endsWith('/toast')) return { showToast: (text, type) => toasts.push({ text, type }) };
      if (name.endsWith('/safeMode')) return { safeModeEnabled };
      return {};
    },
  };
  const scope = vue.effectScope();
  scope.run(() => runInNewContext(code, context));
  mounted();
  requests[0].resolve({ ok: true, json: async () => [] });
  requests[1].resolve({ ok: true, json: async () => [{ userName: 'User', isCurrentUser: true }] });
  await setImmediate();
  requests.length = 0;
  context.exports.programPath.value = 'C:/app.exe';
  return { subject: context.exports, requests, toasts, safeModeEnabled, dispose: () => { unmount(); scope.stop(); } };
}

test('tag management is opt-in, resets on leaving and is locked by safe mode', async () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  assert.ok(source.includes('<button class="button button-secondary" type="button" @click="tagLaunchTarget = tag">启动</button>'));
  const f = await fixture();
  try {
    assert.equal(f.subject.managingTags.value, false);
    f.subject.toggleTagManagement();
    assert.equal(f.subject.managingTags.value, true);
    f.subject.toggleTagManagement();
    assert.equal(f.subject.managingTags.value, false);
    f.subject.toggleTagManagement();
    f.subject.tagDeleteTarget.value = { name: 'Tag' };
    f.safeModeEnabled.value = true;
    await vue.nextTick();
    assert.equal(f.subject.managingTags.value, false);
    assert.equal(f.subject.tagDeleteTarget.value, undefined);
    f.subject.toggleTagManagement();
    assert.equal(f.subject.managingTags.value, false);
    f.safeModeEnabled.value = false;
    await vue.nextTick();
    assert.equal(f.subject.managingTags.value, false);
    f.subject.toggleTagManagement();
    assert.equal(f.subject.managingTags.value, true);
    assert.equal(f.requests.length, 0);
  } finally { f.dispose(); }
  assert.equal(f.subject.managingTags.value, false);
});

test('program launch rejects blank and relative paths without a request', async () => {
  const f = await fixture();
  try {
    for (const path of ['', ' ', 'app.exe', '.\\app.exe', 'C:app.exe', '\\app.exe']) {
      f.subject.programPath.value = path;
      await f.subject.launch();
      assert.equal(f.requests.length, 0, path);
      assert.equal(f.subject.launchFormError.value, path.trim() ? '程序路径必须是绝对路径' : '请输入程序路径');
    }
  } finally { f.dispose(); }
});

test('program launch accepts drive and network absolute paths', async () => {
  const f = await fixture();
  try {
    for (const path of ['C:\\app.exe', 'C:/app.exe', '\\\\server\\share\\app.exe', '//server/share/app.exe']) {
      f.subject.programPath.value = ` ${path} `;
      const pending = f.subject.launch();
      const request = f.requests.at(-1);
      assert.equal(JSON.parse(request.init.body).programPath, path);
      request.resolve({ ok: true });
      await pending;
      assert.equal(f.subject.launchFormError.value, '');
    }
  } finally { f.dispose(); }
});

test('repeated program validation keeps the error visible and success clears it', async () => {
  for (const operation of ['launch', 'saveTag']) {
    const f = await fixture();
    let stop;
    try {
      f.subject.tagName.value = 'Tag';
      let pending = f.subject[operation]();
      f.requests.at(-1).resolve(new Response(JSON.stringify({ detail: '不支持此文件类型' }), { status: 400 }));
      await pending;
      const messages = [];
      stop = vue.watchEffect(() => messages.push(f.subject.launchFormError.value));
      pending = f.subject[operation]();
      await vue.nextTick();
      assert.equal(f.subject.launchFormError.value, '不支持此文件类型', operation);
      const body = Promise.withResolvers();
      f.requests.at(-1).resolve({ ok: false, json: () => body.promise });
      await setImmediate();
      assert.equal(f.subject.launchFormError.value, '不支持此文件类型', operation);
      body.resolve({ detail: '起始目录不存在' });
      await pending;
      assert.deepEqual(messages, ['不支持此文件类型', '起始目录不存在']);
      pending = f.subject[operation]();
      f.requests.at(-1).resolve({ ok: true });
      await setImmediate();
      if (operation === 'saveTag') {
        assert.equal(f.requests.at(-1).url, '/api/programs/tags');
        f.requests.at(-1).resolve({ ok: true, json: async () => [] });
      }
      await pending;
      assert.equal(f.subject.launchFormError.value, '');
    } finally { stop?.(); f.dispose(); }
  }
});

test('tag launches report each result only through toast and ignore duplicate submissions', async () => {
  const f = await fixture();
  try {
    const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
    for (const removed of ['tagLaunchError', 'tagLaunchMessage', 'tag-launch-result'])
      assert.equal(source.includes(removed), false);
    const tag = { name: 'Tag' };
    let pending = f.subject.launchTag(tag);
    f.requests.at(-1).resolve(new Response(JSON.stringify({ detail: '找不到指定的程序文件' }), { status: 400 }));
    await pending;
    assert.deepEqual(f.toasts, [{ text: '找不到指定的程序文件', type: undefined }]);
    pending = f.subject.launchTag(tag);
    await f.subject.launchTag(tag);
    await vue.nextTick();
    assert.equal(f.requests.length, 2);
    assert.equal(f.toasts.length, 1);
    const body = Promise.withResolvers();
    f.requests.at(-1).resolve({ ok: false, json: () => body.promise });
    await setImmediate();
    assert.equal(f.toasts.length, 1);
    body.resolve({ detail: '起始目录不存在' });
    await pending;
    assert.deepEqual(f.toasts.at(-1), { text: '起始目录不存在', type: undefined });
    pending = f.subject.launchTag(tag);
    f.requests.at(-1).resolve({ ok: true });
    await pending;
    assert.deepEqual(f.toasts.at(-1), { text: '标签已启动', type: 'success' });
    assert.equal(f.toasts.length, 3);
    assert.equal(f.subject.pendingActions.size, 0);
  } finally { f.dispose(); }
});

test('editing launch fields clears validation feedback without changing other page messages', async () => {
  const f = await fixture();
  try {
    for (const [field, value] of [
      ['programPath', 'C:/other.exe'], ['argumentsText', '--test'],
      ['workingDirectory', 'C:/Apps'], ['runElevated', true], ['silent', true], ['tagName', 'Other'],
    ]) {
      f.subject.launchFormError.value = '配置无效';
      f.subject.pageError.value = '列表读取失败';
      f.subject.launchFormMessage.value = '程序已启动';
      f.subject[field].value = value;
      await vue.nextTick();
      assert.equal(f.subject.launchFormError.value, '', field);
      assert.equal(f.subject.pageError.value, '列表读取失败');
      assert.equal(f.subject.launchFormMessage.value, '程序已启动');
    }
    assert.equal(f.requests.length, 0);
  } finally { f.dispose(); }
});

test('late launch and tag validation cannot restore errors after the form changes', async () => {
  for (const operation of ['launch', 'saveTag']) {
    const f = await fixture();
    try {
      f.subject.tagName.value = 'Tag';
      const pending = f.subject[operation]();
      f.subject.launchFormError.value = '旧提示';
      f.subject.programPath.value = 'C:/other.exe';
      await vue.nextTick();
      assert.equal(f.subject.launchFormError.value, '');
      f.requests.at(-1).resolve(new Response(JSON.stringify({ detail: '不支持此文件类型' }), { status: 400 }));
      await pending;
      assert.equal(f.subject.launchFormError.value, '');
      assert.equal(f.toasts.length, 0);
      const retry = f.subject[operation]();
      f.requests.at(-1).resolve(new Response(JSON.stringify({ detail: '找不到指定的程序文件' }), { status: 400 }));
      await retry;
      assert.equal(f.subject.launchFormError.value, '找不到指定的程序文件');
    } finally { f.dispose(); }
  }
});

test('program writes do not cancel each other and duplicate submissions are ignored', async () => {
  const f = await fixture();
  try {
    f.subject.tagName.value = 'Tag';
    const launch = f.subject.launch();
    const save = f.subject.saveTag();
    await f.subject.launch();
    await f.subject.saveTag();
    assert.equal(f.requests.length, 2);
    assert.ok(f.requests.every(request => !request.init.signal.aborted));
    f.requests[0].resolve({ ok: true });
    f.requests[1].resolve({ ok: true });
    await setImmediate();
    f.requests[2].resolve({ ok: true, json: async () => [] });
    await Promise.all([launch, save]);
    assert.equal(f.subject.pendingActions.size, 0);
    assert.equal(f.toasts.length, 2);
  } finally { f.dispose(); }
});

test('tag editing scrolls without focusing, cancel and save reset the form, and failed saves retain input', async () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  const panel = source.match(/<section ref="launchPanel"[^>]*>([\s\S]*?)<\/section>/)?.[1];
  assert.ok(panel?.includes('重新编辑已保存的标签配置'));
  assert.ok(panel?.includes('class="launch-form"'));
  const f = await fixture();
  try {
    let scrolls = 0;
    f.subject.launchPanel.value = { scrollIntoView() { scrolls++; }, focus() { assert.fail('editing must not focus'); } };
    const tag = { name: 'Tag', programPath: 'C:/app.exe', arguments: '--test', workingDirectory: 'C:/', runElevated: true, silent: true };
    function assertFormCleared() {
      for (const field of ['tagName', 'programPath', 'argumentsText', 'workingDirectory', 'launchFormError', 'launchFormMessage'])
        assert.equal(f.subject[field].value, '', field);
      assert.equal(f.subject.runElevated.value, false);
      assert.equal(f.subject.silent.value, false);
      assert.equal(f.subject.editingTagName.value, undefined);
    }
    await f.subject.editTag(tag);
    assert.equal(scrolls, 1);
    assert.equal(f.subject.editingTagName.value, 'Tag');
    assert.equal(f.subject.programPath.value, tag.programPath);
    f.subject.cancelTagEdit();
    assertFormCleared();
    assert.equal(scrolls, 1);
    await f.subject.editTag(tag);
    const save = f.subject.saveTag();
    assert.equal(f.requests[0].url, '/api/programs/tags/Tag');
    assert.equal(f.requests[0].init.method, 'PUT');
    f.subject.cancelTagEdit();
    assert.equal(f.subject.editingTagName.value, 'Tag');
    f.requests[0].resolve({ ok: true });
    await setImmediate();
    f.requests[1].resolve({ ok: true, json: async () => [tag] });
    await save;
    assertFormCleared();
    assert.equal(scrolls, 2);
    await f.subject.editTag(tag);
    const failedSave = f.subject.saveTag();
    f.requests.at(-1).resolve({ ok: false, json: async () => ({ detail: '路径无效' }) });
    await failedSave;
    assert.equal(f.subject.editingTagName.value, tag.name);
    for (const [field, value] of Object.entries({ tagName: tag.name, programPath: tag.programPath, argumentsText: tag.arguments, workingDirectory: tag.workingDirectory, runElevated: true, silent: true }))
      assert.equal(f.subject[field].value, value, field);
    assert.equal(f.subject.launchFormError.value, '路径无效');
    assert.equal(scrolls, 3);
  } finally { f.dispose(); }
});

test('closing a usage dialog prevents a late result from repopulating it', async () => {
  const f = await fixture();
  try {
    f.subject.showUsage({ name: 'App', processIds: [1] });
    assert.equal(f.requests.length, 0);
    assert.equal(f.subject.processUsage.value, undefined);
    assert.equal(f.subject.isUsageLoading.value, false);
    const pending = f.subject.refreshUsage();
    f.subject.closeUsage();
    assert.equal(f.requests[0].init.signal.aborted, true);
    f.requests[0].resolve({ ok: true, json: async () => ({ cpuPercent: 1 }) });
    await pending;
    assert.equal(f.subject.processUsageTarget.value, undefined);
    assert.equal(f.subject.processUsage.value, undefined);
    assert.equal(f.subject.isUsageLoading.value, false);
  } finally { f.dispose(); }
});

test('usage results remain until the dialog closes and a new dialog starts empty', async () => {
  const f = await fixture();
  try {
    const target = { name: 'App', processIds: [1] };
    f.subject.showUsage(target);
    const pending = f.subject.refreshUsage();
    await f.subject.refreshUsage();
    assert.equal(f.requests.length, 1);
    f.requests[0].resolve({ ok: true, json: async () => ({ cpuPercent: 1, memoryBytes: 100, gpuPercent: null }) });
    await pending;
    assert.equal(f.subject.processUsage.value.cpuPercent, 1);
    assert.equal(f.subject.isUsageLoading.value, false);
    assert.equal(f.requests.length, 1);
    f.subject.closeUsage();
    assert.equal(f.subject.processUsage.value, undefined);
    f.subject.showUsage(target);
    assert.equal(f.subject.processUsage.value, undefined);
    assert.equal(f.requests.length, 1);
  } finally { f.dispose(); }
});

test('collapsing a query invalidates its result and releases the querying state', async () => {
  const f = await fixture();
  try {
    const pending = f.subject.queryPrograms();
    f.subject.collapsePrograms();
    f.requests[0].resolve({ ok: true, json: async () => [{ name: 'App', processId: 1 }] });
    await pending;
    assert.equal(f.subject.hasQueried.value, false);
    assert.equal(f.subject.isQuerying.value, false);
    assert.equal(f.subject.programs.value.length, 0);
  } finally { f.dispose(); }
});

test('leaving the page aborts all independent writes without late state changes or toasts', async () => {
  const f = await fixture();
  const launch = f.subject.launch();
  const tag = f.subject.launchTag({ name: 'Tag' });
  f.dispose();
  assert.ok(f.requests.every(request => request.init.signal.aborted));
  f.requests[0].resolve({ ok: true });
  f.requests[1].reject(new Error('late failure'));
  await Promise.all([launch, tag]);
  assert.equal(f.toasts.length, 0);
  assert.equal(f.subject.launchFormError.value, '');
  assert.equal(f.subject.pendingActions.size, 0);
});

test('program grouping preserves all processes and alphabetical ordering', async () => {
  const f = await fixture();
  try {
    f.subject.programs.value = [{ name: 'B', processId: 1 }, { name: 'A', processId: 2 }, { name: 'B', processId: 3 }];
    assert.equal(f.subject.groups.value.map(group => group.name).join(','), 'A,B');
    assert.equal(f.subject.groups.value[1].processes.map(process => process.processId).join(','), '1,3');
  } finally { f.dispose(); }
});
