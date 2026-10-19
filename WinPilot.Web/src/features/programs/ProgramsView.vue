<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowReactive, watch } from 'vue';
import AppModal from '../../components/AppModal.vue';
import AppSelect from '../../components/AppSelect.vue';
import HelpHint from '../../components/HelpHint.vue';
import { readApiError, apiFetch, isAbortError, userErrorMessage } from '../../api/client';
import { showToast } from '../../ui/toast';
import AppIcon from '../../components/AppIcon.vue';
import { safeModeEnabled, safeModeMessage } from '../security/safeMode';

interface RunningProgram {
  processId: number;
  name: string;
  userName: string;
  sessionId: number;
}
interface InteractiveUser {
  userName: string;
  isCurrentUser: boolean;
}
interface ProgramUsage {
  processIds: number[];
  name: string;
  cpuPercent: number;
  memoryBytes: number;
  gpuPercent: number | null;
}
interface ProgramTag {
  name: string;
  programPath: string;
  arguments: string;
  workingDirectory: string;
  runElevated: boolean;
  silent: boolean;
}
interface ProgramGroup {
  name: string;
  processes: RunningProgram[];
}
interface ProcessTarget {
  name: string;
  processIds: number[];
  singleProcess?: RunningProgram;
}

const programs = ref<RunningProgram[]>([]);
const users = ref<InteractiveUser[]>([]);
const selectedUserName = ref('');
const tags = ref<ProgramTag[]>([]);
const tagsLoaded = ref(false);
const programSearch = ref('');
const pageSize = ref(10);
const currentPage = ref(1);
const expandedGroups = ref<string[]>([]);
const hasQueried = ref(false);
const isQuerying = ref(false);
const isUsageLoading = ref(false);
type ProgramAction = 'launch' | 'save-tag' | 'close' | 'delete-tag' | 'launch-tag';
const pendingActions = shallowReactive(new Map<ProgramAction, AbortController>());
const isLaunching = computed(() => pendingActions.has('launch'));
const isClosing = computed(() => pendingActions.has('close'));
const processUsageTarget = ref<ProcessTarget>();
const processUsage = ref<ProgramUsage>();
const processCloseTarget = ref<ProcessTarget>();
const tagDeleteTarget = ref<ProgramTag>();
const pageError = ref('');
const launchFormError = ref('');
const processMessage = ref('');
const launchFormMessage = ref('');
const tagLaunchTarget = ref<ProgramTag>();
const tagSearch = ref('');
const managingTags = ref(false);
const editingTagName = ref<string>();
const launchPanel = ref<HTMLElement>();
const programPath = ref('');
const argumentsText = ref('');
const workingDirectory = ref('');
const runElevated = ref(false);
const silent = ref(false);
const tagName = ref('');
let launchFormVersion = 0;
watch([programPath, argumentsText, workingDirectory, runElevated, silent, tagName], () => {
  launchFormVersion++;
  launchFormError.value = '';
}, { flush: 'sync' });
let isPageActive = false;
let programsController: AbortController | undefined;
let usersController: AbortController | undefined;
let tagsController: AbortController | undefined;
let usageController: AbortController | undefined;

const groups = computed<ProgramGroup[]>(() => {
  const grouped = new Map<string, RunningProgram[]>();
  for (const process of programs.value) {
    const group = grouped.get(process.name);
    if (group) group.push(process);
    else grouped.set(process.name, [process]);
  }
  return [...grouped.entries()]
    .map(([name, processes]) => ({ name, processes }))
    .sort((left, right) => left.name.localeCompare(right.name));
});
const filteredGroups = computed(() => {
  const keyword = programSearch.value.trim().toLocaleLowerCase();
  return keyword
    ? groups.value.filter((group) => group.name.toLocaleLowerCase().includes(keyword))
    : groups.value;
});
const pageCount = computed(() => Math.max(1, Math.ceil(filteredGroups.value.length / pageSize.value)));
const pagedGroups = computed(() =>
  filteredGroups.value.slice((currentPage.value - 1) * pageSize.value, currentPage.value * pageSize.value),
);
const resultSummary = computed(() =>
  programSearch.value.trim()
    ? `查询：${programs.value.length} 个进程 · 结果：${filteredGroups.value.length} 个进程组`
    : `查询：${programs.value.length} 个进程 · ${groups.value.length} 个进程组`,
);
const filteredTags = computed(() => {
  const keyword = tagSearch.value.trim().toLocaleLowerCase();
  return keyword ? tags.value.filter((tag) => tag.name.toLocaleLowerCase().includes(keyword)) : tags.value;
});

watch([programSearch, pageSize], () => {
  currentPage.value = 1;
});
watch(pageCount, (count) => {
  if (currentPage.value > count) currentPage.value = count;
});
watch(selectedUserName, () => {
  if (hasQueried.value) collapsePrograms();
});
watch(safeModeEnabled, (enabled) => {
  if (enabled) {
    usersController?.abort();
    usersController = undefined;
    collapsePrograms();
    processCloseTarget.value = undefined;
    processUsageTarget.value = undefined;
    tagDeleteTarget.value = undefined;
    managingTags.value = false;
    editingTagName.value = undefined;
  } else if (isPageActive && !users.value.length) void loadUsers();
});
onMounted(() => {
  isPageActive = true;
  void (safeModeEnabled.value ? loadTags() : Promise.all([loadTags(), loadUsers()]));
});
onBeforeUnmount(releasePageResources);

function releasePageResources() {
  isPageActive = false;
  programsController?.abort();
  usersController?.abort();
  tagsController?.abort();
  usageController?.abort();
  for (const controller of pendingActions.values()) controller.abort();
  pendingActions.clear();
  programs.value = [];
  users.value = [];
  tags.value = [];
  processUsageTarget.value = undefined;
  processUsage.value = undefined;
  processCloseTarget.value = undefined;
  tagDeleteTarget.value = undefined;
  tagLaunchTarget.value = undefined;
  tagSearch.value = '';
  managingTags.value = false;
}
function toggleTagManagement() {
  if (!safeModeEnabled.value) managingTags.value = !managingTags.value;
}
function setListError(error: unknown) {
  if (isPageActive && !isAbortError(error)) pageError.value = userErrorMessage(error, '操作失败，请稍后重试');
}
async function performAction(key: ProgramAction, url: string, init: RequestInit, success: () => void | Promise<void>, failure: (error: unknown) => void) {
  if (!isPageActive || pendingActions.has(key)) return;
  const controller = new AbortController();
  pendingActions.set(key, controller);
  try {
    const response = await apiFetch(url, { ...init, signal: controller.signal, headers: { 'Content-Type': 'application/json' } });
    if (!response.ok) throw new Error(await readApiError(response, '操作失败，请稍后重试'));
    if (isPageActive && !controller.signal.aborted) await success();
  } catch (error) {
    if (isPageActive && !controller.signal.aborted && !isAbortError(error)) failure(error);
  } finally {
    if (pendingActions.get(key) === controller) pendingActions.delete(key);
  }
}
function targetForGroup(group: ProgramGroup): ProcessTarget {
  return { name: group.name, processIds: group.processes.map((process) => process.processId) };
}
function targetForProcess(process: RunningProgram): ProcessTarget {
  return { name: process.name, processIds: [process.processId], singleProcess: process };
}

async function loadTags() {
  tagsController?.abort();
  const controller = new AbortController();
  tagsController = controller;
  try {
    const response = await apiFetch('/api/programs/tags', { signal: controller.signal });
    if (!response.ok) throw new Error(await readApiError(response, '操作失败，请稍后重试'));
    const nextTags = (await response.json()) as ProgramTag[];
    if (isPageActive && tagsController === controller) {
      tags.value = nextTags;
      tagsLoaded.value = true;
    }
  } catch (error) {
    if (tagsController === controller) setListError(error);
  } finally {
    if (tagsController === controller) tagsController = undefined;
  }
}
async function loadUsers() {
  usersController?.abort();
  const controller = new AbortController();
  usersController = controller;
  try {
    const response = await apiFetch('/api/programs/users', { signal: controller.signal });
    if (!response.ok) throw new Error(await readApiError(response, '操作失败，请稍后重试'));
    const nextUsers = (await response.json()) as InteractiveUser[];
    if (isPageActive && usersController === controller) {
      users.value = nextUsers;
      selectedUserName.value =
        nextUsers.find((user) => user.isCurrentUser)?.userName ?? nextUsers[0]?.userName ?? '';
    }
  } catch (error) {
    if (usersController === controller) setListError(error);
  } finally {
    if (usersController === controller) usersController = undefined;
  }
}
async function queryPrograms() {
  if (!isPageActive || safeModeEnabled.value) return;
  if (!selectedUserName.value) {
    pageError.value = '当前没有可查询的交互式用户';
    return;
  }
  programsController?.abort();
  const controller = new AbortController();
  programsController = controller;
  isQuerying.value = true;
  pageError.value = '';
  processMessage.value = '';
  try {
    const response = await apiFetch(`/api/programs?userName=${encodeURIComponent(selectedUserName.value)}`, {
      signal: controller.signal,
    });
    if (!response.ok) throw new Error(await readApiError(response, '操作失败，请稍后重试'));
    const nextPrograms = (await response.json()) as RunningProgram[];
    if (isPageActive && programsController === controller) {
      programs.value = nextPrograms;
      hasQueried.value = true;
      expandedGroups.value = [];
      currentPage.value = 1;
    }
  } catch (error) {
    if (programsController === controller) setListError(error);
  } finally {
    if (programsController === controller) {
      programsController = undefined;
      if (isPageActive) isQuerying.value = false;
    }
  }
}
function collapsePrograms() {
  programsController?.abort();
  programsController = undefined;
  isQuerying.value = false;
  programs.value = [];
  expandedGroups.value = [];
  hasQueried.value = false;
  programSearch.value = '';
  currentPage.value = 1;
  closeUsage();
}
function toggleGroup(name: string) {
  expandedGroups.value = expandedGroups.value.includes(name)
    ? expandedGroups.value.filter((item) => item !== name)
    : [...expandedGroups.value, name];
}
function isExpanded(name: string) {
  return expandedGroups.value.includes(name);
}
function showUsage(target: ProcessTarget) {
  if (!isPageActive || safeModeEnabled.value) return;
  closeUsage();
  processUsageTarget.value = target;
}
async function refreshUsage() {
  const target = processUsageTarget.value;
  if (!isPageActive || safeModeEnabled.value || !target || isUsageLoading.value) return;
  usageController?.abort();
  const controller = new AbortController();
  usageController = controller;
  isUsageLoading.value = true;
  pageError.value = '';
  try {
    const response = await apiFetch('/api/programs/usage', {
      method: 'POST',
      body: JSON.stringify({ processIds: target.processIds }),
      signal: controller.signal,
      headers: { 'Content-Type': 'application/json' },
    });
    if (!response.ok) throw new Error(await readApiError(response, '操作失败，请稍后重试'));
    const usage = (await response.json()) as ProgramUsage;
    if (isPageActive && usageController === controller) processUsage.value = usage;
  } catch (error) {
    if (usageController === controller) setListError(error);
  } finally {
    if (usageController === controller) {
      usageController = undefined;
      if (isPageActive) isUsageLoading.value = false;
    }
  }
}
function closeUsage() {
  usageController?.abort();
  usageController = undefined;
  processUsageTarget.value = undefined;
  processUsage.value = undefined;
  isUsageLoading.value = false;
}
async function confirmClose(force: boolean) {
  if (safeModeEnabled.value || !processCloseTarget.value || isClosing.value) return;
  const target = processCloseTarget.value;
  pageError.value = '';
  await performAction('close', '/api/programs/close', {
    method: 'POST', body: JSON.stringify({ processIds: target.processIds, force }),
  }, async () => {
    if (processCloseTarget.value === target) processCloseTarget.value = undefined;
    closeUsage();
    processMessage.value = force ? `已强制关闭 ${target.name}` : `已关闭 ${target.name}`;
    showToast(processMessage.value, 'success');
    await queryPrograms();
  }, error => {
    setListError(error);
    showToast(userErrorMessage(error, '关闭进程失败，请稍后重试'));
  });
}
function launchPayload() {
  return {
    programPath: programPath.value.trim(),
    arguments: argumentsText.value.trim(),
    workingDirectory: workingDirectory.value.trim(),
    runElevated: runElevated.value,
    silent: silent.value,
  };
}
async function launch() {
  if (safeModeEnabled.value || isLaunching.value) return;
  const submittedVersion = launchFormVersion;
  launchFormMessage.value = '';
  const payload = launchPayload();
  if (!payload.programPath || !/^(?:[a-zA-Z]:[\\/]|[\\/]{2})/.test(payload.programPath)) {
    launchFormError.value = payload.programPath ? '程序路径必须是绝对路径' : '请输入程序路径';
    showToast(launchFormError.value);
    return;
  }
  await performAction('launch', '/api/programs/launch', {
    method: 'POST', body: JSON.stringify(payload),
  }, () => {
    launchFormError.value = '';
    launchFormMessage.value = '程序已启动';
    showToast(launchFormMessage.value, 'success');
  }, error => {
    if (launchFormVersion !== submittedVersion) return;
    launchFormError.value = userErrorMessage(error, '启动失败，请稍后重试');
    showToast(launchFormError.value);
  });
}
async function saveTag() {
  if (safeModeEnabled.value || pendingActions.has('save-tag')) return;
  const submittedVersion = launchFormVersion;
  const payload = { name: tagName.value.trim(), ...launchPayload() };
  const originalName = editingTagName.value;
  launchFormMessage.value = '';
  const url = originalName ? `/api/programs/tags/${encodeURIComponent(originalName)}` : '/api/programs/tags';
  await performAction('save-tag', url, {
    method: originalName ? 'PUT' : 'POST', body: JSON.stringify(payload),
  }, async () => {
    launchFormError.value = '';
    launchFormMessage.value = '标签已保存';
    showToast(launchFormMessage.value, 'success');
    if (editingTagName.value === originalName && tagName.value.trim() === payload.name) resetLaunchForm();
    await loadTags();
  }, error => {
    if (launchFormVersion !== submittedVersion) return;
    launchFormError.value = userErrorMessage(error, '保存标签失败，请稍后重试');
    showToast(launchFormError.value);
  });
}
async function editTag(tag: ProgramTag) {
  if (safeModeEnabled.value || pendingActions.has('save-tag')) return;
  editingTagName.value = tag.name;
  tagName.value = tag.name;
  programPath.value = tag.programPath;
  argumentsText.value = tag.arguments;
  workingDirectory.value = tag.workingDirectory;
  runElevated.value = tag.runElevated;
  silent.value = tag.silent;
  launchFormError.value = launchFormMessage.value = '';
  await nextTick();
  if (isPageActive && !safeModeEnabled.value && editingTagName.value === tag.name)
    launchPanel.value?.scrollIntoView({ block: 'start' });
}
function cancelTagEdit() {
  if (pendingActions.has('save-tag')) return;
  resetLaunchForm();
}
function resetLaunchForm() {
  editingTagName.value = undefined;
  tagName.value = '';
  programPath.value = '';
  argumentsText.value = '';
  workingDirectory.value = '';
  runElevated.value = false;
  silent.value = false;
  launchFormError.value = launchFormMessage.value = '';
}
async function confirmDeleteTag() {
  if (safeModeEnabled.value || !tagDeleteTarget.value) return;
  const tag = tagDeleteTarget.value;
  await performAction('delete-tag', `/api/programs/tags/${encodeURIComponent(tag.name)}`, {
    method: 'DELETE',
  }, async () => {
    if (tagDeleteTarget.value === tag) tagDeleteTarget.value = undefined;
    if (editingTagName.value === tag.name) editingTagName.value = undefined;
    await loadTags();
    if (isPageActive) showToast(`已删除标签 ${tag.name}`, 'success');
  }, error => {
    setListError(error);
    showToast(userErrorMessage(error, '删除标签失败，请稍后重试'));
  });
}
async function launchTag(tag: ProgramTag) {
  if (pendingActions.has('launch-tag')) return;
  await performAction('launch-tag', `/api/programs/tags/${encodeURIComponent(tag.name)}/launch`, {
    method: 'POST',
  }, () => {
    showToast('标签已启动', 'success');
  }, error => {
    showToast(userErrorMessage(error, '启动标签失败，请稍后重试'));
  });
}
async function confirmLaunchTag() {
  if (!tagLaunchTarget.value) return;
  const tag = tagLaunchTarget.value;
  tagLaunchTarget.value = undefined;
  await launchTag(tag);
}
function formatMemory(bytes: number) {
  return `${(bytes / 1024 / 1024).toFixed(0)} MB`;
}
</script>

<template>
  <section class="programs-view">
    <p v-if="pageError" class="status-message error" role="alert">{{ pageError }}</p>
    <p v-else-if="processMessage" class="status-message success" role="status">{{ processMessage }}</p>
    <section class="program-panel" :class="{ 'safe-mode-locked': safeModeEnabled }" :inert="safeModeEnabled">
      <div class="program-panel-header">
        <div>
          <h2>
            当前进程
            <span v-if="safeModeEnabled" class="safe-mode-lock-icon" :title="safeModeMessage">
              <AppIcon name="lock" />
            </span>
          </h2>
          <p>选择交互式用户后按需查询进程</p>
        </div>
        <div class="program-actions process-query-actions">
          <label class="field-control user-select">
            <span>用户</span>
            <AppSelect
              v-model="selectedUserName"
              :options="users.map((user) => ({ value: user.userName, label: user.userName }))"
              :disabled="safeModeEnabled || !users.length || isQuerying"
            />
          </label>
          <button
            class="button button-primary"
            type="button"
            :disabled="safeModeEnabled || isQuerying || !selectedUserName"
            @click="queryPrograms"
          >
            {{ isQuerying ? '查询中…' : '查询' }}
          </button>
          <button v-if="hasQueried" class="button button-secondary" type="button" @click="collapsePrograms">
            收起
          </button>
        </div>
      </div>
      <template v-if="hasQueried">
        <div class="program-query-bar">
          <label class="field-control program-search">
            <input v-model="programSearch" type="search" aria-label="搜索进程名" placeholder="搜索当前结果" />
          </label>
        </div>
        <p class="program-result-summary">{{ resultSummary }}</p>
        <div v-if="pagedGroups.length" class="program-list">
          <article v-for="group in pagedGroups" :key="group.name" class="program-group">
            <div class="program-row">
              <span class="program-name">
                {{ group.name }}
              </span>
              <button
                v-if="group.processes.length > 1"
                type="button"
                class="process-count"
                :aria-expanded="isExpanded(group.name)"
                :aria-label="`${group.name} 共 ${group.processes.length} 个进程`"
                @click="toggleGroup(group.name)"
              >
                {{ group.processes.length }}
              </button>
              <button
                class="button button-secondary"
                type="button"
                @click="showUsage(targetForGroup(group))"
              >
                查看
              </button>
            </div>
            <div v-if="isExpanded(group.name)" class="process-children">
              <div v-for="process in group.processes" :key="process.processId" class="process-child">
                <span class="process-detail">
                  <span>PID {{ process.processId }}</span>
                </span>
                <button
                  class="button button-secondary"
                  type="button"
                  @click="showUsage(targetForProcess(process))"
                >
                  查看
                </button>
              </div>
            </div>
          </article>
        </div>
        <p v-else class="program-empty">没有符合搜索条件的进程</p>
        <div v-if="filteredGroups.length" class="program-pagination">
          <label class="field-control page-size-control">
            <span>每页</span>
            <AppSelect
              v-model="pageSize"
              :options="[10, 20, 50, 100].map((value) => ({ value, label: String(value) }))"
            />
          </label>
          <div class="pagination-actions">
            <button
              class="button button-secondary"
              type="button"
              :disabled="currentPage === 1"
              @click="currentPage--"
            >
              上一页
            </button>
            <span>{{ currentPage }} / {{ pageCount }}</span>
            <button
              class="button button-secondary"
              type="button"
              :disabled="currentPage === pageCount"
              @click="currentPage++"
            >
              下一页
            </button>
          </div>
        </div>
      </template>
      <p v-else class="program-empty">请选择用户后查询进程</p>
    </section>
    <section ref="launchPanel" class="program-panel" :class="{ 'safe-mode-locked': safeModeEnabled }" :inert="safeModeEnabled">
      <div class="program-panel-header">
        <div>
          <h2>
            {{ editingTagName ? `编辑快捷标签「${editingTagName}」` : '启动程序' }}
            <span v-if="safeModeEnabled" class="safe-mode-lock-icon" :title="safeModeMessage">
              <AppIcon name="lock" />
            </span>
          </h2>
          <p>{{ editingTagName ? '重新编辑已保存的标签配置' : '启动指定程序或运行脚本' }}</p>
        </div>
      </div>
      <form class="launch-form" @submit.prevent="editingTagName ? saveTag() : launch()">
        <div class="field-control launch-wide">
          <span>
            <label for="launch-program-path">程序 / 脚本路径</label>
            <HelpHint label="支持 .exe、.bat、.cmd、.vbs 和 .py 文件，.py 需要安装 Python" />
          </span>
          <input
            id="launch-program-path"
            title=""
            v-model="programPath"
            required
            :disabled="safeModeEnabled"
            placeholder="请输入绝对路径"
          />
        </div>
        <label class="field-control">
          <span>启动参数</span>
          <input v-model="argumentsText" :disabled="safeModeEnabled" placeholder="可留空" />
        </label>
        <label class="field-control">
          <span>起始于</span>
          <input v-model="workingDirectory" :disabled="safeModeEnabled" placeholder="可留空" />
        </label>
        <div class="launch-options">
          <label class="toggle-control">
            <input v-model="runElevated" type="checkbox" :disabled="safeModeEnabled" />
            <span>使用最高权限运行</span>
          </label>
          <label class="toggle-control">
            <input v-model="silent" type="checkbox" :disabled="safeModeEnabled" />
            <span>静默执行</span>
          </label>
        </div>
        <label class="field-control">
          <span>标签名称</span>
          <input v-model="tagName" :disabled="safeModeEnabled" :placeholder="editingTagName ? '请输入标签名称' : '创建标签时填写'" />
        </label>
        <p v-if="launchFormError" class="launch-error" role="alert">{{ launchFormError }}</p>
        <p v-else-if="launchFormMessage" class="launch-result success" role="status">{{ launchFormMessage }}</p>
        <div class="launch-actions">
          <button v-if="editingTagName" class="button button-primary" type="submit" :disabled="safeModeEnabled || pendingActions.has('save-tag')">
            {{ pendingActions.has('save-tag') ? '正在保存…' : '保存标签' }}
          </button>
          <button v-else class="button button-primary" type="submit" :disabled="safeModeEnabled || isLaunching">
            {{ isLaunching ? '正在启动…' : '启动程序' }}
          </button>
          <button v-if="editingTagName" class="button button-secondary" type="button" :disabled="safeModeEnabled || pendingActions.has('save-tag')" @click="cancelTagEdit">
            取消编辑
          </button>
          <button v-else class="button button-secondary" type="button" :disabled="safeModeEnabled || pendingActions.has('save-tag')" @click="saveTag">
            创建标签
          </button>
        </div>
      </form>
    </section>
    <section class="program-panel">
      <div class="program-panel-header">
        <div>
          <h2>快捷标签</h2>
          <p>快捷启动保存的标签</p>
        </div>
        <div v-if="tags.length" class="tag-controls">
          <label class="tag-search">
            <input v-model="tagSearch" type="search" aria-label="搜索标签名" placeholder="搜索标签名" />
          </label>
          <button
            class="button button-secondary"
            :class="{ 'safe-mode-button': safeModeEnabled }"
            type="button"
            :disabled="safeModeEnabled"
            :title="safeModeEnabled ? safeModeMessage : undefined"
            :aria-pressed="managingTags"
            @click="toggleTagManagement"
          >
            <AppIcon v-if="safeModeEnabled" name="lock" />
            {{ managingTags ? '完成' : '管理' }}
          </button>
        </div>
      </div>
      <div v-if="filteredTags.length" class="tag-list">
        <article v-for="tag in filteredTags" :key="tag.name" class="tag-row">
          <div class="tag-info">
            <strong>{{ tag.name }}</strong>
            <small v-if="managingTags && !safeModeEnabled" :title="tag.programPath">{{ tag.programPath }}</small>
          </div>
          <div class="program-actions">
            <button class="button button-secondary" type="button" @click="tagLaunchTarget = tag">启动</button>
            <template v-if="managingTags && !safeModeEnabled">
              <button class="button button-secondary" type="button" @click="editTag(tag)">编辑</button>
              <button class="button button-danger" type="button" @click="tagDeleteTarget = tag">删除</button>
            </template>
          </div>
        </article>
      </div>
      <p v-else-if="tags.length" class="program-empty">没有符合搜索条件的标签</p>
      <p v-else-if="tagsLoaded && !pageError" class="program-empty">{{ tags.length ? '没有匹配的快捷标签' : '暂无快捷标签' }}</p>
    </section>
    <AppModal v-if="processUsageTarget" title="进程占用情况" @close="closeUsage">
      <p class="modal-program-name">
        {{ processUsageTarget.name }}
        <small v-if="processUsageTarget.singleProcess">PID {{ processUsageTarget.singleProcess.processId }}</small>
      </p>
      <dl class="usage-grid">
        <div>
          <dt>CPU 占用</dt>
          <dd>{{ processUsage ? `${processUsage.cpuPercent.toFixed(1)}%` : '—' }}</dd>
        </div>
        <div>
          <dt>内存占用</dt>
          <dd>{{ processUsage ? formatMemory(processUsage.memoryBytes) : '—' }}</dd>
        </div>
        <div>
          <dt>显卡占用</dt>
          <dd>
            {{ !processUsage ? '—' : processUsage.gpuPercent === null ? '不可用' : `${processUsage.gpuPercent.toFixed(1)}%` }}
          </dd>
        </div>
      </dl>
      <template #actions>
        <button
          class="button button-primary"
          type="button"
          :disabled="isUsageLoading"
          @click="refreshUsage"
        >
          {{ isUsageLoading ? '正在刷新…' : '刷新' }}
        </button>
        <button class="button button-danger" type="button" @click="processCloseTarget = processUsageTarget">
          关闭进程
        </button>
      </template>
    </AppModal>
    <AppModal v-if="processCloseTarget" title="确认关闭进程" :dismissible="!isClosing" @close="processCloseTarget = undefined">
      <p class="modal-program-name">
        {{ processCloseTarget.name }}
        <small v-if="processCloseTarget.singleProcess">PID：{{ processCloseTarget.singleProcess.processId }}</small>
        <small v-else>共 {{ processCloseTarget.processIds.length }} 个进程</small>
      </p>
      <p>“关闭进程”会请求进程正常退出，“强制关闭”会立即结束进程</p>
      <template #actions>
        <button
          class="button button-secondary"
          type="button"
          :disabled="isClosing"
          @click="processCloseTarget = undefined"
        >
          取消
        </button>
        <button
          class="button button-danger"
          type="button"
          :disabled="isClosing"
          @click="confirmClose(false)"
        >
          {{ isClosing ? '正在关闭…' : '关闭进程' }}
        </button>
        <button class="button button-danger" type="button" :disabled="isClosing" @click="confirmClose(true)">
          强制关闭
        </button>
      </template>
    </AppModal>
    <AppModal v-if="tagDeleteTarget" title="确认删除标签" :dismissible="!pendingActions.has('delete-tag')" @close="tagDeleteTarget = undefined">
      <p>确认删除标签“{{ tagDeleteTarget.name }}”吗？</p>
      <template #actions>
        <button class="button button-secondary" type="button" :disabled="pendingActions.has('delete-tag')" @click="tagDeleteTarget = undefined">取消</button>
        <button class="button button-danger" type="button" :disabled="pendingActions.has('delete-tag')" @click="confirmDeleteTag">删除</button>
      </template>
    </AppModal>
    <AppModal v-if="tagLaunchTarget" title="确认启动标签" @close="tagLaunchTarget = undefined">
      <p>确定启动“{{ tagLaunchTarget.name }}”吗？</p>
      <template #actions>
        <button class="button button-secondary" type="button" @click="tagLaunchTarget = undefined">取消</button>
        <button class="button button-primary" type="button" @click="confirmLaunchTag">启动</button>
      </template>
    </AppModal>
  </section>
</template>

<style scoped>
.programs-view {
  width: 100%;
  max-width: none;
}
.tag-controls { display: flex; align-items: center; gap: 8px; width: min(100%, 320px); }
.tag-controls .tag-search { flex: 1; min-width: 0; }
.tag-controls .button { flex-shrink: 0; }
@media (max-width: 767px) {
  .programs-view .program-pagination { flex-direction: row; align-items: center; gap: 8px; }
  .programs-view .page-size-control { width: auto; grid-template-columns: auto 64px; flex: 0 0 auto; }
  .programs-view .pagination-actions { display: flex; width: auto; gap: 6px; }
  .programs-view .pagination-actions .button { width: auto; min-width: 68px; padding-inline: 8px; white-space: nowrap; }
  .tag-info small { white-space: normal; overflow-wrap: anywhere; text-overflow: clip; }
  .tag-controls { width: 100%; }
  .process-query-actions .user-select { grid-template-columns: minmax(0, 1fr); flex: 0 0 100%; min-width: 0; }
  .process-query-actions .button { min-width: 0; white-space: nowrap; }
}
</style>
