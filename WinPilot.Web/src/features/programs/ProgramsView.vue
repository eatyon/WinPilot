<script setup lang="ts">
import { useDelayedLoading } from '../../ui/useDelayedLoading';
import AppLoadError from '../../components/AppLoadError.vue';
import { useModalState } from '../../ui/useModalState';
import AppLoading from '../../components/AppLoading.vue';
import { useId, computed, nextTick, onBeforeUnmount, onMounted, ref, shallowReactive, watch } from 'vue';
import { NFormItem, NEmpty, NButton, NCard, NCheckbox, NCollapseTransition, NInput, NModal, NPagination, NSelect, NSkeleton, NSpace, NText } from 'naive-ui';
import HelpHint from '../../components/HelpHint.vue';
import { selectFallbackOption, withSelectLabels } from '../../ui/selectLabel';
import { readApiError, apiFetch, isAbortError, userErrorMessage } from '../../api/client';
import { showToast } from '../../ui/toast';
import { safeModeEnabled } from '../security/safeMode';
import type { RunningProgram, ProgramGroup } from './programModels';
import { useProgramList } from './useProgramList';

const formId = useId();

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
interface ProcessTarget {
  name: string;
  processName: string;
  processIds: number[];
  singleProcess?: RunningProgram;
  canEnd: boolean;
}

const programs = ref<RunningProgram[]>([]);
const users = ref<InteractiveUser[]>([]);
const selectedUserName = ref('');
const tags = ref<ProgramTag[]>([]);
const tagsLoaded = ref(false);
const programList = useProgramList(programs);
const {
  programSearch, pageSize, currentPage, expandedGroups, filteredGroups, pageCount, pagedGroups, resultSummary,
} = programList;
const hasQueried = ref(false);
const isQuerying = ref(false);
const queryError = ref('');
const isUsageLoading = ref(false);
type ProgramAction = 'launch' | 'save-tag' | 'close' | 'delete-tag' | 'launch-tag';
const pendingActions = shallowReactive(new Map<ProgramAction, AbortController>());
const isLaunching = computed(() => pendingActions.has('launch'));
const isClosing = computed(() => pendingActions.has('close'));
const processUsageTargetModal = useModalState<ProcessTarget | undefined>(() => undefined, Boolean, clearUsageContent);
const processUsageTarget = processUsageTargetModal.data;
const processUsage = ref<ProgramUsage>();
const usageError = ref('');
const processCloseTargetModal = useModalState<ProcessTarget | undefined>(() => undefined);
const processCloseTarget = processCloseTargetModal.data;
const tagDeleteTargetModal = useModalState<ProgramTag | undefined>(() => undefined);
const tagDeleteTarget = tagDeleteTargetModal.data;
const pageError = ref('');
const launchFormError = ref('');
const launchErrorField = ref<'programPath' | 'tagName' | ''>('');
const tagLaunchTargetModal = useModalState<ProgramTag | undefined>(() => undefined);
const tagLaunchTarget = tagLaunchTargetModal.data;
const launchingTagName = ref('');
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
  launchErrorField.value = '';
}, { flush: 'sync' });
let isPageActive = false;
let programsController: AbortController | undefined;
let usersController: AbortController | undefined;
let tagsController: AbortController | undefined;
let usageController: AbortController | undefined;

const filteredTags = computed(() => {
  const keyword = tagSearch.value.trim().toLocaleLowerCase();
  return keyword ? tags.value.filter((tag) => tag.name.toLocaleLowerCase().includes(keyword)) : tags.value;
});

watch(selectedUserName, () => {
  if (hasQueried.value) collapsePrograms();
});
watch(safeModeEnabled, (enabled) => {
  if (enabled) {
    usersController?.abort();
    usersController = undefined;
    collapsePrograms();
    processCloseTargetModal.reset();
    processUsageTargetModal.reset();
    tagDeleteTargetModal.reset();
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
  processUsageTargetModal.reset();
  processUsage.value = undefined;
  processCloseTargetModal.reset();
  tagDeleteTargetModal.reset();
  tagLaunchTargetModal.reset();
  tagSearch.value = '';
  managingTags.value = false;
}
function toggleTagManagement() {
  if (!safeModeEnabled.value) managingTags.value = !managingTags.value;
}
async function retryPage() {
  const retryQuery = Boolean(queryError.value);
  if (pageError.value) {
    const loaded = await Promise.all([loadUsers(), loadTags()]);
    if (!loaded.every(Boolean)) return;
    pageError.value = '';
  }
  if (retryQuery && !pageError.value) await queryPrograms();
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
  return {
    name: group.name,
    processName: group.processName,
    processIds: group.processes.map((process) => process.processId),
    singleProcess: group.processes.length === 1 ? group.processes[0] : undefined,
    canEnd: group.processes.every(process => process.canEnd),
  };
}
// 徽标统计实际可展开的进程行和窗口行，单进程组不重复计入主行。
function groupDetailCount(group: ProgramGroup) {
  const processRows = group.processes.length > 1 ? group.processes.length : 0;
  return processRows + group.processes.reduce((total, process) => total + process.windowTitles.length, 0);
}
function targetForProcess(process: RunningProgram): ProcessTarget {
  return {
    name: process.displayName,
    processName: process.name,
    processIds: [process.processId],
    singleProcess: process,
    canEnd: process.canEnd,
  };
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
      if (!nextTags.length) managingTags.value = false;
      tagsLoaded.value = true;
      return true;
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
      return true;
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
  try {
    const response = await apiFetch(`/api/programs?userName=${encodeURIComponent(selectedUserName.value)}`, {
      signal: controller.signal,
    });
    if (!response.ok) throw new Error(await readApiError(response, '操作失败，请稍后重试'));
    const nextPrograms = (await response.json()) as RunningProgram[];
    if (isPageActive && programsController === controller) {
      programs.value = nextPrograms;
      queryError.value = '';
      pageError.value = '';
      hasQueried.value = true;
      expandedGroups.value = [];
      currentPage.value = 1;
    }
  } catch (error) {
    if (isPageActive && programsController === controller && !isAbortError(error)) {
      queryError.value = userErrorMessage(error, '进程查询失败');
    }
  } finally {
    if (programsController === controller) {
      programsController = undefined;
      if (isPageActive) isQuerying.value = false;
    }
  }
}
function collapsePrograms() {
  queryError.value = '';
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
  processUsageTargetModal.open(target);
}
async function refreshUsage() {
  const target = processUsageTarget.value;
  if (!isPageActive || safeModeEnabled.value || !target || isUsageLoading.value) return;
  usageController?.abort();
  const controller = new AbortController();
  usageController = controller;
  isUsageLoading.value = true;
  try {
    const response = await apiFetch('/api/programs/usage', {
      method: 'POST',
      body: JSON.stringify({ processIds: target.processIds }),
      signal: controller.signal,
      headers: { 'Content-Type': 'application/json' },
    });
    if (!response.ok) throw new Error(await readApiError(response, '操作失败，请稍后重试'));
    const usage = (await response.json()) as ProgramUsage;
    if (isPageActive && usageController === controller) {
      processUsage.value = usage;
      usageError.value = '';
    }
  } catch (error) {
    if (isPageActive && usageController === controller && !isAbortError(error)) {
      usageError.value = userErrorMessage(error, '占用情况刷新失败');
    }
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
  processUsageTargetModal.close();
}
function clearUsageContent() {
  usageError.value = '';
  processUsage.value = undefined;
  isUsageLoading.value = false;
}
async function confirmClose() {
  if (safeModeEnabled.value || !processCloseTarget.value?.canEnd || isClosing.value) return;
  const target = processCloseTarget.value;
  pageError.value = '';
  const isGroup = !target.singleProcess;
  await performAction('close', '/api/programs/close', {
    method: 'POST', body: JSON.stringify({ processIds: target.processIds, isGroup }),
  }, async () => {
    if (processCloseTarget.value === target) processCloseTargetModal.close();
    closeUsage();
    showToast(`已结束 ${target.name}`, 'success');
    await queryPrograms();
  }, error => {
    showToast(userErrorMessage(error, '结束进程失败，请稍后重试'));
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
// 输入改变后不再显示旧提交的错误，防止后端响应把红字写回已修改的表单。
function setLaunchFormFailure(error: unknown, fallback: string, submittedVersion: number) {
  if (launchFormVersion !== submittedVersion) return;
  launchFormError.value = userErrorMessage(error, fallback);
  showToast(launchFormError.value);
}
function validateLaunchForm(path: string, name?: string) {
  launchErrorField.value = '';
  if (!path || !/^(?:[a-zA-Z]:[\\/]|[\\/]{2})/.test(path)) {
    launchErrorField.value = 'programPath';
    launchFormError.value = path ? '程序路径必须是绝对路径' : '请输入程序路径';
    return false;
  }
  if (name !== undefined && !name) {
    launchErrorField.value = 'tagName';
    launchFormError.value = '请输入标签名称';
    return false;
  }
  return true;
}
async function launch() {
  if (safeModeEnabled.value || isLaunching.value) return;
  const submittedVersion = launchFormVersion;
  const payload = launchPayload();
  if (!validateLaunchForm(payload.programPath)) return;
  await performAction('launch', '/api/programs/launch', {
    method: 'POST', body: JSON.stringify(payload),
  }, () => {
    launchFormError.value = '';
    showToast('程序已启动', 'success');
  }, error => {
    setLaunchFormFailure(error, '启动失败，请稍后重试', submittedVersion);
  });
}
async function saveTag() {
  if (safeModeEnabled.value || pendingActions.has('save-tag')) return;
  const submittedVersion = launchFormVersion;
  const payload = { name: tagName.value.trim(), ...launchPayload() };
  const originalName = editingTagName.value;
  if (!validateLaunchForm(payload.programPath, payload.name)) return;
  const url = originalName ? `/api/programs/tags/${encodeURIComponent(originalName)}` : '/api/programs/tags';
  await performAction('save-tag', url, {
    method: originalName ? 'PUT' : 'POST', body: JSON.stringify(payload),
  }, async () => {
    launchFormError.value = '';
    showToast('标签已保存', 'success');
    if (editingTagName.value === originalName && tagName.value.trim() === payload.name) resetLaunchForm();
    await loadTags();
  }, error => {
    setLaunchFormFailure(error, '保存标签失败，请稍后重试', submittedVersion);
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
  launchFormError.value = '';
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
  launchFormError.value = '';
}
async function confirmDeleteTag() {
  if (safeModeEnabled.value || !tagDeleteTarget.value) return;
  const tag = tagDeleteTarget.value;
  await performAction('delete-tag', `/api/programs/tags/${encodeURIComponent(tag.name)}`, {
    method: 'DELETE',
  }, async () => {
    if (tagDeleteTarget.value === tag) tagDeleteTargetModal.close();
    if (editingTagName.value === tag.name) editingTagName.value = undefined;
    await loadTags();
    if (isPageActive) showToast('标签已删除', 'success');
  }, error => {
    showToast(userErrorMessage(error, '删除标签失败，请稍后重试'));
  });
}
async function launchTag(tag: ProgramTag) {
  if (!isPageActive || pendingActions.has('launch-tag')) return;
  launchingTagName.value = tag.name;
  try {
    await performAction('launch-tag', `/api/programs/tags/${encodeURIComponent(tag.name)}/launch`, {
      method: 'POST',
    }, () => {
      showToast('标签已启动', 'success');
    }, error => {
      showToast(userErrorMessage(error, '启动标签失败，请稍后重试'));
    });
  } finally { launchingTagName.value = ''; }
}
async function confirmLaunchTag() {
  if (!tagLaunchTarget.value) return;
  const tag = tagLaunchTarget.value;
  tagLaunchTargetModal.close();
  await launchTag(tag);
}
function formatMemory(bytes: number) {
  return `${(bytes / 1024 / 1024).toFixed(0)} MB`;
}
const showUsageLoading = useDelayedLoading(() => isUsageLoading.value);
</script>

<template>
  <section class="programs-view load-error-layout">
    <AppLoadError
      :show="Boolean(pageError || queryError)"
      :retry="retryPage"
      :loading="isQuerying"
    >{{ queryError || pageError }}</AppLoadError>
    <section class="program-section" :class="{ 'safe-mode-locked': safeModeEnabled }" :inert="safeModeEnabled">
      <NCard>
        <div class="content-section-header">
          <div>
            <h2>
              当前进程
            </h2>
            <p>选择交互式用户后按需查询进程</p>
          </div>
          <div class="content-actions process-query-actions">
            <NFormItem
              label="用户"
              :label-props="{ id: (`${formId}-field-0`) + '-label', class: 'user-select-label' }"
              label-placement="left"
              :show-feedback="false"
              class="form-field user-select"
            >
              <NSelect
                :fallback-option="selectFallbackOption"
                :aria-labelledby="(`${formId}-field-0`) + '-label'"
                :input-props="{ id: `${formId}-field-0` }"
                v-model:value="selectedUserName"
                :options="withSelectLabels(users.map((user) => ({ value: user.userName, label: user.userName })))"
                :disabled="safeModeEnabled || !users.length || isQuerying"
              />
            </NFormItem>
            <NButton
              class="program-button"
              :loading="isQuerying"
              type="primary"
              attr-type="button"
              :disabled="safeModeEnabled || isQuerying || !selectedUserName"
              @click="queryPrograms"
            >
              {{ isQuerying ? '查询中' : '查询' }}
            </NButton>
            <NButton v-if="hasQueried" class="program-button" attr-type="button" @click="collapsePrograms">
              收起
            </NButton>
          </div>
        </div>
        <AppLoading v-if="isQuerying && !hasQueried && !queryError" label="正在查询进程" />
        <template v-else-if="hasQueried">
          <div class="program-query-bar">
            <label class="form-field process-search">
              <NInput
                v-model:value="programSearch"
                clearable
                :input-props="{ role: 'searchbox', enterkeyhint: 'search', 'aria-label': '搜索进程名' }"
                placeholder="搜索当前结果"
              />
            </label>
          </div>
          <p class="program-result-summary">{{ resultSummary }}</p>
          <div v-if="pagedGroups.length" class="program-list">
            <article v-for="group in pagedGroups" :key="group.id" class="program-group">
              <div class="program-row" :class="{ 'is-sticky': isExpanded(group.id) }">
                <span class="program-name program-identity">
                  <span>{{ group.name }}</span>
                  <small v-if="group.processName !== group.name">{{ group.processName }}</small>
                </span>
                <NButton
                  v-if="groupDetailCount(group) > 0"
                  attr-type="button"
                  size="tiny"
                  round
                  :type="isExpanded(group.id) ? 'primary' : 'default'"
                  :secondary="isExpanded(group.id)"
                  class="process-count"
                  :aria-expanded="isExpanded(group.id)"
                  :aria-label="`${group.name} 共 ${groupDetailCount(group)} 项，${isExpanded(group.id) ? '收起详情' : '展开查看'}`"
                  @click="toggleGroup(group.id)"
                >
                  {{ groupDetailCount(group) }}
                </NButton>
                <NButton
                  class="program-button"
                  type="default"
                  attr-type="button"
                  @click="showUsage(targetForGroup(group))"
                >
                  查看
                </NButton>
              </div>
              <NCollapseTransition :show="isExpanded(group.id)" :inert="!isExpanded(group.id) || undefined">
                <div class="process-children">
                  <div v-for="process in group.processes" :key="process.processId" class="process-entry">
                    <div v-if="group.processes.length > 1" class="process-child">
                      <span class="process-detail program-identity">
                        <span>{{ process.displayName }}</span>
                        <small v-if="process.name !== process.displayName">{{ process.name }}</small>
                      </span>
                      <NButton
                        class="program-button"
                        type="default"
                        attr-type="button"
                        @click="showUsage(targetForProcess(process))"
                      >
                        查看
                      </NButton>
                    </div>
                    <ul v-if="process.windowTitles.length" class="process-windows" :aria-label="`${process.displayName} 的窗口`">
                      <li v-for="(title, index) in process.windowTitles" :key="index" class="process-child">
                        <span class="process-detail program-identity">
                          <span>{{ title }}</span>
                          <small>窗口</small>
                        </span>
                      </li>
                    </ul>
                  </div>
                </div>
              </NCollapseTransition>
            </article>
          </div>
          <NEmpty :show-icon="false" v-else class="content-empty">
            没有符合搜索条件的进程
          </NEmpty>
          <div v-if="filteredGroups.length" class="app-pagination">
            <NFormItem
              label="每页"
              :label-props="{ id: (`${formId}-field-1`) + '-label' }"
              label-placement="left"
              :show-feedback="false"
              class="form-field page-size-control"
            >
              <NSelect
                :fallback-option="selectFallbackOption"
                :aria-labelledby="(`${formId}-field-1`) + '-label'"
                :input-props="{ id: `${formId}-field-1` }"
                v-model:value="pageSize"
                :options="withSelectLabels([10, 20, 50, 100].map((value) => ({ value, label: String(value) })))"
              />
            </NFormItem>
            <NPagination v-model:page="currentPage" :page-count="pageCount" simple aria-label="进程分页" />
          </div>
        </template>
        <NText tag="p" :depth="3" v-else class="content-empty">
          {{ queryError ? '无法加载进程列表' : '请选择用户后查询进程' }}
        </NText>
      </NCard>
    </section>
    <section ref="launchPanel" class="program-section" :class="{ 'safe-mode-locked': safeModeEnabled }" :inert="safeModeEnabled">
      <NCard>
        <div class="content-section-header">
          <div>
            <h2>
              {{ editingTagName ? `编辑快捷标签「${editingTagName}」` : '启动程序' }}
            </h2>
            <p>{{ editingTagName ? '重新编辑已保存的标签配置' : '启动指定程序或运行脚本' }}</p>
          </div>
        </div>
        <form class="launch-form" :inert="pendingActions.has('save-tag')" novalidate @submit.prevent="editingTagName ? saveTag() : launch()">
          <NFormItem
            :label-props="{ for: 'launch-program-path' }"
            required
            :show-require-mark="false"
            :show-feedback="launchErrorField === 'programPath' && Boolean(launchFormError)"
            :validation-status="launchErrorField === 'programPath' && launchFormError ? 'error' : undefined"
            :feedback="launchErrorField === 'programPath' ? launchFormError : undefined"
            class="form-field launch-wide"
          >
            <template #label>
              <span class="field-label-with-hint launch-path-label">
                <span>程序 / 脚本路径<span class="launch-path-required" aria-hidden="true">&nbsp;*</span></span>
                <HelpHint label="支持 .exe、.bat、.cmd、.vbs 和 .py 文件，.py 需要安装 Python" />
              </span>
            </template>
            <NInput
              v-model:value="programPath"
              :input-props="{ id: 'launch-program-path', 'aria-required': true, 'aria-invalid': launchErrorField === 'programPath' && Boolean(launchFormError) || undefined }"
              :disabled="safeModeEnabled"
              placeholder="请输入绝对路径"
            />
          </NFormItem>
          <NFormItem
            label="启动参数"
            :label-props="{ for: `${formId}-field-2` }"
            :show-feedback="false"
            class="form-field"
          >
            <NInput :input-props="{ id: `${formId}-field-2` }" v-model:value="argumentsText" :disabled="safeModeEnabled" placeholder="可留空" />
          </NFormItem>
          <NFormItem
            label="起始于"
            :label-props="{ for: `${formId}-field-3` }"
            :show-feedback="false"
            class="form-field"
          >
            <NInput :input-props="{ id: `${formId}-field-3` }" v-model:value="workingDirectory" :disabled="safeModeEnabled" placeholder="可留空" />
          </NFormItem>
          <div class="form-options">
            <NCheckbox v-model:checked="runElevated" :disabled="safeModeEnabled">使用最高权限运行</NCheckbox>
            <NCheckbox v-model:checked="silent" :disabled="safeModeEnabled">静默执行</NCheckbox>
          </div>
          <NFormItem
            label="标签名称"
            :label-props="{ for: `${formId}-field-4` }"
            :show-feedback="launchErrorField === 'tagName' && Boolean(launchFormError)"
            :validation-status="launchErrorField === 'tagName' && launchFormError ? 'error' : undefined"
            :feedback="launchErrorField === 'tagName' ? launchFormError : undefined"
            class="form-field"
          >
            <NInput
              :input-props="{ id: `${formId}-field-4`, 'aria-invalid': launchErrorField === 'tagName' && Boolean(launchFormError) || undefined }"
              v-model:value="tagName"
              :disabled="safeModeEnabled"
              :placeholder="editingTagName ? '请输入标签名称' : '创建标签时填写'"
            />
          </NFormItem>
          <div class="launch-actions">
            <NButton
              v-if="editingTagName"
              class="program-button"
              :loading="pendingActions.has('save-tag')"
              type="primary"
              attr-type="submit"
              :disabled="safeModeEnabled || pendingActions.has('save-tag')"
            >
              {{ pendingActions.has('save-tag') ? '正在保存' : '保存标签' }}
            </NButton>
            <NButton v-else class="program-button" :loading="isLaunching" type="primary" attr-type="submit" :disabled="safeModeEnabled || isLaunching">
              {{ isLaunching ? '正在启动' : '启动程序' }}
            </NButton>
            <NButton
              v-if="editingTagName"
              class="program-button"
              type="default"
              attr-type="button"
              :disabled="safeModeEnabled || pendingActions.has('save-tag')"
              @click="cancelTagEdit"
            >
              取消编辑
            </NButton>
            <NButton
              v-else
              class="program-button"
              :loading="pendingActions.has('save-tag')"
              type="default"
              attr-type="button"
              :disabled="safeModeEnabled || pendingActions.has('save-tag')"
              @click="saveTag"
            >
              {{ pendingActions.has('save-tag') ? '正在创建' : '创建标签' }}
            </NButton>
          </div>
        </form>
      </NCard>
    </section>
    <section class="program-section">
      <NCard>
        <div class="content-section-header">
          <div>
            <h2>快捷标签</h2>
            <p>快捷启动保存的标签</p>
          </div>
          <div class="tag-controls">
            <label class="tag-search-input">
              <NInput
                v-model:value="tagSearch"
                clearable
                :input-props="{ role: 'searchbox', enterkeyhint: 'search', 'aria-label': '搜索标签名' }"
                placeholder="搜索标签名"
                :disabled="!tags.length"
              />
            </label>
            <NButton
              class="program-button"
              :type="managingTags ? 'primary' : 'default'"
              attr-type="button"
              :disabled="safeModeEnabled || !tags.length"
              :aria-pressed="managingTags"
              @click="toggleTagManagement"
            >
              {{ managingTags ? '完成' : '管理' }}
            </NButton>
          </div>
        </div>
        <div v-if="filteredTags.length" class="tag-list">
          <article v-for="tag in filteredTags" :key="tag.name" class="tag-row">
            <div class="tag-info">
              <strong>{{ tag.name }}</strong>
              <small v-if="managingTags && !safeModeEnabled">{{ tag.programPath }}</small>
            </div>
            <div class="content-actions">
              <NButton
                class="program-button"
                type="default"
                attr-type="button"
                :loading="launchingTagName === tag.name"
                :disabled="Boolean(launchingTagName) || tagLaunchTargetModal.show"
                @click="tagLaunchTargetModal.open(tag)"
              >启动</NButton>
              <template v-if="managingTags && !safeModeEnabled">
                <NButton class="program-button" type="default" attr-type="button" @click="editTag(tag)">编辑</NButton>
                <NButton class="program-button" type="error" attr-type="button" @click="tagDeleteTargetModal.open(tag)">删除</NButton>
              </template>
            </div>
          </article>
        </div>
        <NEmpty :show-icon="false" v-else-if="tags.length" class="content-empty">
          没有符合搜索条件的标签
        </NEmpty>
        <NEmpty :show-icon="false" v-else-if="tagsLoaded && !pageError" class="content-empty">
          {{ tags.length ? '没有匹配的快捷标签' : '暂无快捷标签' }}
        </NEmpty>
      </NCard>
    </section>
    <NModal
      @after-leave="processUsageTargetModal.afterLeave"
      :internal-appear="true"
      v-if="processUsageTarget"
      title="进程占用情况"
      @update:show="show => { if (!show) closeUsage(); }"
      :show="processUsageTargetModal.show"
      preset="card"
      class="app-detail-dialog"
      content-scrollable
      :closable="true"
      :mask-closable="true"
      :close-on-esc="true"
      :inert="!processUsageTargetModal.show || undefined"
    >
      <p class="modal-program-name">
        {{ processUsageTarget.name }}
        <small v-if="processUsageTarget.processName !== processUsageTarget.name">{{ processUsageTarget.processName }}</small>
        <small v-if="processUsageTarget.singleProcess">PID {{ processUsageTarget.singleProcess.processId }}</small>
      </p>
      <AppLoadError :show="Boolean(usageError)">{{ usageError }}</AppLoadError>
      <div class="usage-grid" :aria-busy="isUsageLoading">
        <NCard embedded size="small">
          <dl>
            <dt>CPU 占用</dt>
            <dd><NSkeleton v-if="isUsageLoading && !processUsage && !usageError" :width="64" height="1lh" /><template v-else>{{ processUsage ? `${processUsage.cpuPercent.toFixed(1)}%` : '—' }}</template></dd>
          </dl>
        </NCard>
        <NCard embedded size="small">
          <dl>
            <dt>内存占用</dt>
            <dd><NSkeleton v-if="isUsageLoading && !processUsage && !usageError" :width="64" height="1lh" /><template v-else>{{ processUsage ? formatMemory(processUsage.memoryBytes) : '—' }}</template></dd>
          </dl>
        </NCard>
        <NCard embedded size="small">
          <dl>
            <dt>显卡占用</dt>
            <dd>
              <NSkeleton v-if="isUsageLoading && !processUsage && !usageError" :width="64" height="1lh" />
              <template v-else>{{ !processUsage ? '—' : processUsage.gpuPercent === null ? '不可用' : `${processUsage.gpuPercent.toFixed(1)}%` }}</template>
            </dd>
          </dl>
        </NCard>
      </div>
      <template #footer>
        <NSpace justify="end">
          <NButton
            class="program-button"
            :loading="showUsageLoading"
            :aria-busy="isUsageLoading"
            aria-label="刷新"
            type="primary"
            attr-type="button"
            :disabled="isUsageLoading && !showUsageLoading"
            @click="refreshUsage"
          >
            刷新
          </NButton>
          <NButton
            class="program-button"
            type="error"
            attr-type="button"
            :disabled="!processUsageTarget.canEnd"
            @click="processCloseTargetModal.open(processUsageTarget)"
          >
            {{ processUsageTarget.singleProcess ? '结束进程' : '结束任务' }}
          </NButton>
        </NSpace>
      </template>
    </NModal>
    <NModal
      @after-leave="processCloseTargetModal.afterLeave"
      :internal-appear="true"
      v-if="processCloseTarget"
      :title="processCloseTarget.singleProcess ? '确认结束进程' : '确认结束任务'"
      @update:show="show => { if (!show) processCloseTargetModal.close(); }"
      :show="processCloseTargetModal.show"
      preset="dialog"
      class="app-confirm-dialog"
      :show-icon="false"
      :closable="!isClosing"
      :mask-closable="!isClosing"
      :close-on-esc="!isClosing"
      :inert="!processCloseTargetModal.show || undefined"
    >
      <p class="modal-program-name">
        {{ processCloseTarget.name }}
        <small v-if="processCloseTarget.processName !== processCloseTarget.name">{{ processCloseTarget.processName }}</small>
        <small v-if="processCloseTarget.singleProcess">PID：{{ processCloseTarget.singleProcess.processId }}</small>
        <small v-else>共 {{ processCloseTarget.processIds.length }} 个进程</small>
      </p>
      <p>{{ processCloseTarget.singleProcess ? '将直接结束该进程，未保存的数据可能丢失' : '将请求关闭窗口并结束组内全部进程，未保存的数据可能丢失' }}</p>
      <template #action>
        <NSpace justify="end">
          <NButton
            class="program-button"
            type="default"
            attr-type="button"
            :disabled="isClosing"
            @click="processCloseTargetModal.close()"
          >
            取消
          </NButton>
          <NButton
            class="program-button"
            :loading="isClosing"
            type="error"
            attr-type="button"
            :disabled="isClosing || !processCloseTarget.canEnd"
            @click="confirmClose"
          >
            {{ isClosing ? '正在结束' : '确认结束' }}
          </NButton>
        </NSpace>
      </template>
    </NModal>
    <NModal
      @after-leave="tagDeleteTargetModal.afterLeave"
      :internal-appear="true"
      v-if="tagDeleteTarget"
      title="确认删除标签"
      @update:show="show => { if (!show) tagDeleteTargetModal.close(); }"
      :show="tagDeleteTargetModal.show"
      preset="dialog"
      class="app-confirm-dialog"
      :show-icon="false"
      :closable="!pendingActions.has('delete-tag')"
      :mask-closable="!pendingActions.has('delete-tag')"
      :close-on-esc="!pendingActions.has('delete-tag')"
      :inert="!tagDeleteTargetModal.show || undefined"
    >
      <p>确认删除标签“{{ tagDeleteTarget.name }}”吗？</p>
      <template #action>
        <NSpace justify="end">
          <NButton class="program-button" type="default" attr-type="button" :disabled="pendingActions.has('delete-tag')" @click="tagDeleteTargetModal.close()">取消</NButton>
          <NButton
            class="program-button"
            :loading="pendingActions.has('delete-tag')"
            type="error"
            attr-type="button"
            :disabled="pendingActions.has('delete-tag')"
            @click="confirmDeleteTag"
          >{{ pendingActions.has('delete-tag') ? '正在删除' : '删除' }}</NButton>
        </NSpace>
      </template>
    </NModal>
    <NModal
      @after-leave="tagLaunchTargetModal.afterLeave"
      :internal-appear="true"
      v-if="tagLaunchTarget"
      title="确认启动标签"
      @update:show="show => { if (!show) tagLaunchTargetModal.close(); }"
      :show="tagLaunchTargetModal.show"
      preset="dialog"
      class="app-confirm-dialog"
      :show-icon="false"
      :closable="true"
      :mask-closable="true"
      :close-on-esc="true"
      :inert="!tagLaunchTargetModal.show || undefined"
    >
      <p>确定启动“{{ tagLaunchTarget.name }}”吗？</p>
      <template #action>
        <NSpace justify="end">
          <NButton class="program-button" type="default" attr-type="button" @click="tagLaunchTargetModal.close()">取消</NButton>
          <NButton class="program-button" type="primary" attr-type="button" @click="confirmLaunchTag">启动</NButton>
        </NSpace>
      </template>
    </NModal>
  </section>
</template>

<style>
.program-result-summary, .program-name, .process-detail, .usage-grid dt, .usage-grid dd, .tag-row small, .modal-program-name, .modal-program-name small, .program-identity small, .launch-path-required { transition: color .3s cubic-bezier(.4, 0, .2, 1); }
.programs-view { display: grid; gap: var(--page-card-row-gap) 18px; }
.program-query-bar { display: flex; align-items: end; gap: 12px; margin-top: 18px; }
.program-result-summary { margin: 16px 0 0; color: var(--text-muted); font-size: 13px; }
.program-list { margin-top: 12px; border-top: 1px solid var(--border); }
.program-list, .process-child, .tag-row { transition: border-color .3s cubic-bezier(.4, 0, .2, 1); }
.program-row, .tag-row { display: flex; align-items: center; gap: 12px; }
.program-row { min-height: 58px; border-bottom: 1px solid var(--border); }
.program-name, .process-detail { min-width: 0; flex: 1; overflow: hidden; color: var(--text); font-size: 14px; font-weight: 500; text-align: left; }
.process-count { min-width: 28px; }
.process-children { margin: -1px 0 0 18px; border-left: 2px solid var(--border); background: var(--surface-subtle); }
.process-child { display: flex; min-height: 52px; align-items: center; gap: 12px; padding: 0 18px; border-bottom: 1px solid var(--border); }
.process-detail { font-size: 12px; color: var(--text-muted); }
.usage-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; margin: 18px 0 0; }
.usage-grid dl { margin: 0; }
.usage-grid dt { color: var(--text-muted); font-size: 12px; }
.usage-grid dd { margin: 6px 0 0; color: var(--text); font-size: 24px; font-weight: 400; }
.launch-form { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 14px; margin-top: 18px; }
.launch-form input { width: 100%; }
.launch-wide, .launch-actions { grid-column: 1 / -1; }
.launch-actions { display: flex; justify-content: flex-end; gap: 8px; }
.tag-list { display: grid; margin-top: 12px; }
.tag-row { display: grid; grid-template-columns: minmax(0, 1fr) auto; padding: 10px 0; border-top: 1px solid var(--border); }
.tag-row:last-child { padding-bottom: 0; }
.tag-info { display: grid; min-width: 0; gap: 4px; }
.tag-row strong { font-size: 16px; overflow-wrap: anywhere; font-weight: 500; }
.tag-row small { overflow: hidden; color: var(--text-muted); font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
.modal-program-name { display: grid; gap: 4px; margin: 12px 0 0 !important; color: var(--text) !important; font-size: 14px; font-weight: 500; }
.modal-program-name small { color: var(--text-muted); font-size: 12px; font-weight: 400; }
.user-select { display: grid; grid-template-columns: auto minmax(160px, 260px); align-items: center; gap: 8px; }
@media (max-width: 767px) {
  .program-query-bar { display: block; }
  .usage-grid, .launch-form { grid-template-columns: 1fr; }
  .launch-wide, .launch-actions { grid-column: 1; }
  .launch-actions { display: grid; grid-template-columns: 1fr 1fr; }
  .tag-row { grid-template-columns: 1fr; gap: 8px; align-items: start; }
}
.user-select.n-form-item { gap: 0; }
@media (max-width: 767px) {
  .programs-view .content-section-header { align-items: stretch; flex-direction: column; }
  .process-query-actions { align-items: end; }
  .process-query-actions .user-select { width: 100%; }
}

.form-options { display: flex; flex-wrap: wrap; align-items: center; gap: 12px 24px; }
.app-detail-dialog { width: min(560px, calc(100vw - 32px)); max-height: calc(100svh - 48px); }
</style>

<style scoped>
.launch-path-label { gap: 6px; }
.launch-path-required { color: var(--n-asterisk-color, var(--danger-text)); }
.program-row.is-sticky { position: sticky; top: 0; z-index: 1; background: var(--surface); }
.process-children, .program-row {
  transition:
    background-color .3s cubic-bezier(.4, 0, .2, 1),
    color .3s cubic-bezier(.4, 0, .2, 1),
    border-color .3s cubic-bezier(.4, 0, .2, 1);
}
.program-name.program-identity, .process-detail.program-identity { display: grid; gap: 4px; padding: 8px 0; }
.program-identity small { color: var(--text-muted); font-size: 12px; font-weight: 400; overflow-wrap: anywhere; }
.process-windows { margin: 0; padding: 0; list-style: none; }
.process-windows .process-detail { overflow-wrap: anywhere; }
.programs-view {
  width: 100%;
  max-width: none;
}
.form-field.user-select { grid-template-columns: auto minmax(160px, 260px); align-items: center; }
.process-search { width: min(100%, 360px); }
.form-options { grid-column: 1 / -1; }
.tag-controls { display: flex; align-items: center; gap: 8px; width: min(100%, 320px); }
.tag-controls .tag-search-input { flex: 1; min-width: 0; }
.tag-controls .program-button { flex-shrink: 0; }
@media (max-width: 767px) {
  .tag-info small { white-space: normal; overflow-wrap: anywhere; text-overflow: clip; }
  .tag-controls { width: 100%; }
  .process-query-actions .user-select { grid-template-columns: minmax(0, 1fr); grid-template-areas: 'label' 'blank' 'feedback'; grid-template-rows: auto auto auto; flex: 0 0 100%; min-width: 0; }
  .user-select :deep(.user-select-label) { justify-self: start; min-height: 0; padding: 0 0 8px; }
  .content-actions .program-button, .launch-actions .program-button { flex: 1; }
  .process-search { width: 100%; }
  .process-query-actions .program-button { min-width: 0; white-space: nowrap; }
}
</style>
