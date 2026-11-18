import { readStylesheet, readFeatureStyles } from '../support/stylesheet.mjs';
import assert from 'node:assert/strict';
import * as delayedLoading from '../../src/ui/useDelayedLoading.ts';
import { modalStateModule } from '../support/modalState.mjs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { setImmediate } from 'node:timers/promises';
import { loadScriptModule } from '../support/scriptModule.mjs';
import { readFileSync } from 'node:fs';
import * as vue from 'vue';
import * as programList from '../../src/features/programs/useProgramList.ts';
import { elements, attribute, event } from '../support/template.mjs';
import * as apiErrors from '../support/apiErrors.mjs';
import { parse } from '@vue/compiler-sfc';

test('launch path places the required mark before its help entry', () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  assert.match(source, /:show-require-mark="false"/);
  assert.match(source, /程序 \/ 脚本路径<span class="launch-path-required" aria-hidden="true">&nbsp;\*<\/span><\/span>\s*<HelpHint/);
  assert.match(source, /'aria-required': true/);
  assert.match(source, /\.launch-path-label \{ gap: 6px; \}/);
  assert.match(source, /\.launch-path-required \{ color: var\(--n-asterisk-color, var\(--danger-text\)\); \}/);
});

test('short confirmations use dialog presets without a full-height content scrollbar', () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  const modals = [...source.matchAll(/<NModal\b[\s\S]*?<\/NModal>/g)].map(([modal]) => modal);
  assert.equal(modals.length, 4);
  for (const modal of modals) {
    if (modal.includes('v-if="processUsageTarget"')) {
      assert.match(modal, /content-scrollable/);
      assert.match(modal, /preset="card"/);
      assert.match(modal, /class="app-detail-dialog"/);
      assert.match(modal, /<template #footer>/);
    } else {
      assert.doesNotMatch(modal, /content-scrollable/);
      assert.match(modal, /preset="dialog"/);
      assert.match(modal, /<template #action>/);
    }
  }
});

test('mobile query controls use a full-width user field and one button row without desktop overrides', () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  assert.ok(source.includes('class="content-actions process-query-actions"'));
  assert.ok(source.includes("isQuerying ? '查询中' : '查询'"));
  const scoped = parse(source).descriptor.styles.filter(style => style.scoped).map(style => style.content).join('\n');
  const mobile = scoped.match(/@media \(max-width: 767px\)\s*\{([\s\S]*?)\n\}/)?.[1];
  assert.match(mobile, /\.process-query-actions \.user-select\s*\{[^}]*grid-template-columns:\s*minmax\(0, 1fr\);[^}]*flex:\s*0 0 100%/);
  assert.match(mobile, /\.process-query-actions \.program-button\s*\{[^}]*min-width:\s*0;[^}]*white-space:\s*nowrap/);
  assert.equal(scoped.replace(mobile, '').includes('.process-query-actions .'), false);
  const css = readStylesheet(new URL('../../src/styles/components.css', import.meta.url));
  assert.match(css, /\.content-actions\s*\{[^}]*display:\s*flex;[^}]*flex-wrap:\s*wrap/);
  assert.match(mobile, /\.content-actions \.program-button[^{}]*\{\s*flex:\s*1/);
});

test('tag controls remain mounted and disable by the full list rather than search results', () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  assert.match(source, /<div class="tag-controls">/);
  assert.doesNotMatch(source, /<div v-if="tags.length" class="tag-controls">/);
  assert.match(source, /<NInput\s+v-model:value="tagSearch"[^>]*:disabled="!tags.length"/);
  assert.doesNotMatch(source, /safeModeMessage|safe-mode-lock-icon/);
  assert.match(source, /:disabled="safeModeEnabled \|\| !tags.length"\s*:aria-pressed="managingTags"/);
  assert.match(source, /:type="managingTags \? 'primary' : 'default'"/);
});

test('tag paths appear beneath their names only in management mode', () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  assert.match(source, /<div class="tag-info">\s*<strong>\{\{ tag.name \}\}<\/strong>\s*<small v-if="managingTags && !safeModeEnabled">\{\{ tag.programPath \}\}<\/small>\s*<\/div>/);
  const css = readFeatureStyles();
  assert.match(css, /\.tag-row\s*\{[^}]*grid-template-columns:\s*minmax\(0, 1fr\) auto/);
  assert.match(css, /\.tag-info\s*\{[^}]*display:\s*grid;[^}]*min-width:\s*0/);
  assert.match(css, /\.tag-row small\s*\{[^}]*text-overflow:\s*ellipsis;[^}]*white-space:\s*nowrap/);
  assert.match(css, /\.tag-row strong\s*\{[^}]*font-size:\s*16px/);
  assert.match(source, /\.tag-controls\s*\{[^}]*width:\s*min\(100%, 320px\)/);
  const scoped = parse(source).descriptor.styles.filter(style => style.scoped).map(style => style.content).join('\n');
  const mobile = scoped.match(/@media \(max-width: 767px\)\s*\{([\s\S]*?)\n\}/)?.[1];
  assert.match(mobile, /\.tag-info small\s*\{[^}]*white-space:\s*normal;[^}]*overflow-wrap:\s*anywhere/);
  assert.match(mobile, /\.tag-controls\s*\{\s*width:\s*100%/);
});

test('process search appears with query results and uses the tag search font', () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  assert.doesNotMatch(source, /用户列表加载失败/);
  assert.match(source, /queryError \? '无法加载进程列表' : '请选择用户后查询进程'/);
  assert.match(source, /<template v-else-if="hasQueried">\s*<div class="program-query-bar">/);
  assert.match(source, /<NInput\s+v-model:value="programSearch"[^>]*'aria-label': '搜索进程名'/);
  assert.equal(source.includes(':disabled="!hasQueried"'), false);
  assert.match(source, /<NInput\s+v-model:value="tagSearch"/);
});

test('programs and files delegate page navigation and boundary controls to library pagination', () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  const files = readFileSync(new URL('../../src/features/files/FilesView.vue', import.meta.url), 'utf8');
  const css = readFeatureStyles();
  assert.match(source, /<NPagination v-model:page="currentPage" :page-count="pageCount" simple aria-label="进程分页"/);
  assert.match(files, /<NPagination v-model:page="page" :page-count="pageCount" simple aria-label="文件分页"/);
  assert.doesNotMatch(source + files, /@click="(?:currentPage|page)(?:\+\+|--)"/);
  assert.doesNotMatch(css, /program-pagination|pagination-actions|page-size-control/);
});

async function fixture() {
  const code = loadScriptModule(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url),
'tagDeleteTargetModal, processUsageTargetModal, launch, saveTag, launchTag, queryPrograms, collapsePrograms, showUsage, refreshUsage, closeUsage, processUsage, processUsageTarget, isUsageLoading, isQuerying, hasQueried, programs, programList, selectedUserName, tagName, pendingActions, launchFormError, pageError, editTag, cancelTagEdit, editingTagName, launchPanel, programPath, argumentsText, workingDirectory, runElevated, silent, managingTags, toggleTagManagement, tagDeleteTarget, confirmDeleteTag, confirmClose, processCloseTarget, isClosing, filteredGroups, programSearch, targetForGroup, currentPage, pageSize, expandedGroups, toggleGroup, resultSummary, queryError, usageError, groupDetailCount, retryPage');
  let mounted;
  let unmount;
  const requests = [];
  const toasts = [];
  const safeModeEnabled = vue.ref(false);
  const context = {
    exports: {}, AbortController,
    require: name => {
      if (name.endsWith('/useDelayedLoading')) return delayedLoading;
      if (name.endsWith('/useModalState')) return modalStateModule;
      if (name === './useProgramList') return programList;
      if (name === 'vue') return { ...vue, useId: () => 'programs-test', onMounted: callback => { mounted = callback; }, onBeforeUnmount: callback => { unmount = callback; } };
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
  context.exports.groups = context.exports.programList.groups;
  mounted();
  requests[0].resolve({ ok: true, json: async () => [] });
  requests[1].resolve({ ok: true, json: async () => [{ userName: 'User', isCurrentUser: true }] });
  await setImmediate();
  requests.length = 0;
  context.exports.programPath.value = 'C:/app.exe';
  return { subject: context.exports, requests, toasts, safeModeEnabled, dispose: () => { unmount(); scope.stop(); } };
}

test('page retry reloads failed resources or repeats the process query', async () => {
  const f = await fixture();
  try {
    f.subject.pageError.value = 'load failed';
    const resources = f.subject.retryPage();
    assert.equal(f.subject.pageError.value, 'load failed');
    assert.equal(f.requests.length, 2);
    f.requests.find(request => request.url.endsWith('/tags')).resolve({ ok: true, json: async () => [] });
    f.requests.find(request => request.url.endsWith('/users')).resolve({ ok: true, json: async () => [{ userName: 'User', isCurrentUser: true }] });
    await resources;
    assert.equal(f.subject.pageError.value, '');
    f.requests.length = 0;
    f.subject.queryError.value = 'query failed';
    const query = f.subject.retryPage();
    assert.equal(f.requests.length, 1);
    assert.match(f.requests[0].url, /userName=User/);
    f.requests[0].resolve({ ok: true, json: async () => [] });
    await query;
    assert.equal(f.subject.queryError.value, '');
  } finally {
    f.dispose();
  }
});

test('query failure has its own error state until retry succeeds or the list is collapsed', async () => {
  const f = await fixture();
  try {
    f.subject.hasQueried.value = true;
    const failed = f.subject.queryPrograms();
    f.requests.at(-1).reject(new Error('query failed'));
    await failed;
    assert.equal(f.subject.queryError.value, 'query failed');
    f.subject.pageError.value = '';
    assert.equal(f.subject.queryError.value, 'query failed');
    const retry = f.subject.queryPrograms();
    assert.equal(f.subject.queryError.value, 'query failed');
    assert.equal(f.subject.isQuerying.value, true);
    f.requests.at(-1).resolve({ ok: true, json: async () => [] });
    await retry;
    assert.equal(f.subject.queryError.value, '');
    assert.equal(f.subject.isQuerying.value, false);
    f.subject.queryError.value = 'query failed';
    f.subject.collapsePrograms();
    assert.equal(f.subject.queryError.value, '');
  } finally { f.dispose(); }
});

test('tag management is opt-in, resets on leaving and is locked by safe mode', async () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  const launchButton = elements(source, node => node.tag === 'NButton' && event(node, 'click') === 'tagLaunchTargetModal.open(tag)')[0];
  assert.ok(launchButton);
  assert.equal(attribute(launchButton, 'type'), 'default');
  assert.equal(attribute(launchButton, 'attr-type'), 'button');
  assert.ok(launchButton.children.some(node => node.type === 2 && node.content.trim() === '启动'));
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

test('deleting the last tag exits management while remaining tags keep management open', async () => {
  for (const remaining of [[], [{ name: 'Remaining', programPath: 'C:/app.exe' }]]) {
    const f = await fixture();
    try {
      f.subject.managingTags.value = true;
      f.subject.tagDeleteTarget.value = { name: 'Tag' };
      const deleting = f.subject.confirmDeleteTag();
      assert.equal(f.requests[0].init.method, 'DELETE');
      f.requests[0].resolve({ ok: true });
      await setImmediate();
      assert.equal(f.requests[1].url, '/api/programs/tags');
      f.requests[1].resolve({ ok: true, json: async () => remaining });
      await deleting;
      assert.equal(f.subject.managingTags.value, remaining.length > 0);
      assert.equal(f.subject.tagDeleteTargetModal.show, false);
      f.subject.tagDeleteTargetModal.afterLeave();
      assert.equal(f.subject.tagDeleteTarget.value, undefined);
      assert.equal(f.toasts.at(-1).text, '标签已删除');
    } finally { f.dispose(); }
  }
});

test('process ending selects the method from the target and prevents duplicate requests', async () => {
  for (const isGroup of [false, true]) {
    const f = await fixture();
    try {
      f.subject.processCloseTarget.value = { name: 'App', processIds: [1], singleProcess: isGroup ? undefined : { processId: 1 }, canEnd: true };
      const closing = f.subject.confirmClose();
      assert.equal(f.subject.isClosing.value, true);
      await f.subject.confirmClose();
      assert.equal(f.requests.length, 1);
      assert.deepEqual(JSON.parse(f.requests[0].init.body), { processIds: [1], isGroup });
      f.requests[0].reject(new Error('close failed'));
      await closing;
      assert.equal(f.subject.isClosing.value, false);
      assert.ok(f.subject.processCloseTarget.value);
    } finally { f.dispose(); }
  }
});

test('protected members disable ending the group without blocking other members', async () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  const css = readFeatureStyles();
  assert.match(source, /:disabled="!processUsageTarget.canEnd"/);
  assert.match(source, /:disabled="isClosing \|\| !processCloseTarget.canEnd"/);
  assert.doesNotMatch(css, /\.program-end-button:disabled/);
  assert.match(source, /<NButton[^>]*type="error"[^>]*:disabled="!processUsageTarget.canEnd"/);
  const f = await fixture();
  try {
    const allowed = { processId: 1, canEnd: true };
    const protectedProcess = { processId: 2, canEnd: false };
    const group = { name: 'App', processName: 'app.exe', processes: [allowed, protectedProcess] };
    const target = f.subject.targetForGroup(group);
    assert.equal(target.canEnd, false);
    assert.equal(f.subject.targetForGroup({ ...group, processes: [allowed] }).canEnd, true);
    assert.equal(f.subject.targetForGroup({ ...group, processes: [protectedProcess] }).canEnd, false);
    f.subject.processCloseTarget.value = target;
    await f.subject.confirmClose();
    assert.equal(f.requests.length, 0);
    assert.equal(f.subject.isClosing.value, false);
  } finally { f.dispose(); }
});

test('program launch and tag saves reject blank and relative paths without requests or toasts', async () => {
  for (const operation of ['launch', 'saveTag']) {
    const f = await fixture();
    try {
      f.subject.tagName.value = 'Tag';
      for (const path of ['', ' ', 'app.exe', '.\\app.exe', 'C:app.exe', '\\app.exe']) {
        f.subject.programPath.value = path;
        await f.subject[operation]();
        assert.equal(f.requests.length, 0, path);
        assert.equal(f.subject.launchFormError.value, path.trim() ? '程序路径必须是绝对路径' : '请输入程序路径');
        assert.equal(f.toasts.length, 0);
      }
    } finally { f.dispose(); }
  }
});

test('creating and editing tags require a name before sending a request', async () => {
  for (const originalName of [undefined, 'Existing']) {
    const f = await fixture();
    try {
      f.subject.editingTagName.value = originalName;
      for (const name of ['', '  ']) {
        f.subject.tagName.value = name;
        await f.subject.saveTag();
        assert.equal(f.subject.launchFormError.value, '请输入标签名称');
        assert.equal(f.subject.editingTagName.value, originalName);
        assert.equal(f.requests.length, 0);
        assert.equal(f.toasts.length, 0);
      }
    } finally { f.dispose(); }
  }
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
      assert.deepEqual(f.toasts.at(-1), { text: '程序已启动', type: 'success' });
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
      assert.deepEqual(f.toasts.at(-1), { text: operation === 'launch' ? '程序已启动' : '标签已保存', type: 'success' });
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
      f.subject[field].value = value;
      await vue.nextTick();
      assert.equal(f.subject.launchFormError.value, '', field);
      assert.equal(f.subject.pageError.value, '列表读取失败');
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
      for (const field of ['tagName', 'programPath', 'argumentsText', 'workingDirectory', 'launchFormError'])
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
    assert.equal(f.subject.processUsageTargetModal.show, false);
    f.subject.processUsageTargetModal.afterLeave();
    assert.equal(f.subject.processUsageTarget.value, undefined);
    assert.equal(f.subject.processUsage.value, undefined);
    assert.equal(f.subject.isUsageLoading.value, false);
  } finally { f.dispose(); }
});

test('usage results remain until the dialog closes and a new dialog starts empty', async () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  const dialog = source.split('title="进程占用情况"')[1].split('</NModal>')[0];
  assert.equal(dialog.match(/<NSkeleton v-if="isUsageLoading && !processUsage && !usageError"/g).length, 3);
  assert.match(dialog, /class="usage-grid" :aria-busy="isUsageLoading"/);
  assert.doesNotMatch(dialog, /AppLoading/);
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
    assert.equal(f.subject.processUsageTargetModal.show, false);
    assert.equal(f.subject.processUsage.value.cpuPercent, 1);
    f.subject.processUsageTargetModal.afterLeave();
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

test('changing pages or page size collapses groups without refetching or clearing results', async () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  assert.match(source, /class="program-row" :class="\{ 'is-sticky': isExpanded\(group.id\) \}"/);
  assert.match(source, /\.program-row\.is-sticky\s*\{[^}]*position:\s*sticky;[^}]*top:\s*0;[^}]*z-index:\s*1;[^}]*background:\s*var\(--surface\)/);
  const f = await fixture();
  try {
    f.subject.programs.value = Array.from({ length: 21 }, (_, index) => ({
      processId: index + 1, name: 'app.exe', displayName: 'App', groupId: String(index),
      groupName: 'App', groupProcessName: 'app.exe', windowTitles: ['窗口'],
    }));
    await vue.nextTick();
    for (const page of [2, 1]) {
      f.subject.toggleGroup('0');
      assert.equal(f.subject.expandedGroups.value.length, 1);
      f.subject.currentPage.value = page;
      await vue.nextTick();
      assert.equal(f.subject.expandedGroups.value.length, 0);
    }
    f.subject.toggleGroup('0');
    f.subject.pageSize.value = 20;
    await vue.nextTick();
    assert.equal(f.subject.expandedGroups.value.length, 0);
    assert.equal(f.subject.programs.value.length, 21);
    assert.equal(f.requests.length, 0);
  } finally { f.dispose(); }
});

test('program grouping follows backend membership and name order without merging identical names', async () => {
  const css = readFeatureStyles();
  assert.match(css, /\.process-child\s*\{[^}]*padding:\s*0 18px/);
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  assert.match(source, /<li v-for="\(title, index\) in process.windowTitles"[^>]*class="process-child">\s*<span class="process-detail program-identity">\s*<span>\{\{ title \}\}<\/span>\s*<small>窗口<\/small>/);
  assert.doesNotMatch(source, /window-prefix|窗口：/);
  assert.doesNotMatch(source, /process-windows\.nested|nested: group.processes/);
  assert.doesNotMatch(source, /PID \{\{ process\.processId \}\}/);
  assert.match(source, /PID \{\{ processUsageTarget\.singleProcess\.processId \}\}/);
  assert.match(source, /<small v-if="processUsageTarget\.processName !== processUsageTarget\.name">\{\{ processUsageTarget\.processName \}\}<\/small>/);
  assert.match(source, /<small v-if="processCloseTarget\.processName !== processCloseTarget\.name">\{\{ processCloseTarget\.processName \}\}<\/small>/);
  const f = await fixture();
  try {
    f.subject.programs.value = [
      { name: 'node.exe', displayName: 'Node.js', processId: 2, groupId: 'app:1', groupName: 'App', groupProcessName: 'app.exe', windowTitles: [] },
      { name: 'app.exe', displayName: 'App', processId: 1, groupId: 'app:1', groupName: 'App', groupProcessName: 'app.exe', windowTitles: ['编辑窗口'] },
      { name: 'app.exe', displayName: 'App', processId: 3, groupId: 'app:3', groupName: 'App', groupProcessName: 'app.exe', windowTitles: [] },
      { name: 'conhost.exe', displayName: '控制台窗口主机', processId: 4, groupId: 'app:4', groupName: '控制台窗口主机', groupProcessName: 'conhost.exe', windowTitles: ['终端窗口'] },
    ];
    const names = f.subject.groups.value.map(group => group.name);
    assert.equal(names.join(','), [...names].sort((left, right) => left.localeCompare(right)).join(','));
    const merged = f.subject.groups.value.find(group => group.id === 'app:1');
    const background = f.subject.groups.value.find(group => group.id === 'app:3');
    const windowed = f.subject.groups.value.find(group => group.id === 'app:4');
    assert.equal(merged.processName, 'app.exe');
    assert.equal(f.subject.targetForGroup(merged).processName, 'app.exe');
    assert.equal(f.subject.targetForGroup(merged).singleProcess, undefined);
    assert.equal(f.subject.targetForGroup(background).singleProcess.processId, 3);
    assert.equal(merged.processes.map(process => process.processId).join(','), '2,1');
    assert.equal(f.subject.groups.value.flatMap(group => group.processes).length, 4);
    assert.equal(f.subject.resultSummary.value, '查询：4 个进程 · 3 个进程组');
    assert.equal(f.subject.groupDetailCount(merged), 3);
    assert.equal(f.subject.groupDetailCount(background), 0);
    assert.equal(f.subject.groupDetailCount(windowed), 1);
    assert.equal(f.subject.groupDetailCount({ processes: [{ windowTitles: ['窗口一', '窗口二'] }] }), 2);
    assert.equal(f.subject.groupDetailCount({ processes: [{ windowTitles: [] }, { windowTitles: [] }] }), 2);
    assert.match(source, /\{\{ groupDetailCount\(group\) \}\}/);
    assert.match(source, /v-if="groupDetailCount\(group\) > 0"/);
    assert.match(source, /共 \$\{groupDetailCount\(group\)\} 项，\$\{isExpanded\(group.id\) \? '收起详情' : '展开查看'\}/);
    f.subject.programSearch.value = '编辑窗口';
    assert.equal(f.subject.resultSummary.value, '搜索：2 个进程 · 1 个进程组');
    assert.equal(f.subject.filteredGroups.value.length, 1);
    assert.equal(f.subject.targetForGroup(f.subject.filteredGroups.value[0]).processIds.join(','), '2,1');
    f.subject.programSearch.value = '终端';
    assert.equal(f.subject.filteredGroups.value[0].processes[0].processId, 4);
    f.subject.programSearch.value = '不存在';
    assert.equal(f.subject.resultSummary.value, '搜索：0 个进程 · 0 个进程组');
    f.subject.programSearch.value = '';
    assert.equal(f.subject.resultSummary.value, '查询：4 个进程 · 3 个进程组');
  } finally { f.dispose(); }
});
test('shortcut tag paths remain visible without hover hints', () => {
  const source = readFileSync(new URL('../../src/features/programs/ProgramsView.vue', import.meta.url), 'utf8');
  assert.match(source, /<small v-if="managingTags && !safeModeEnabled">\{\{ tag\.programPath \}\}<\/small>/);
  assert.doesNotMatch(source, /(?:text|title)="tag\.programPath"/);
});
