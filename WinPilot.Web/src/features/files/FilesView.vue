<script setup lang="ts">
import { useDelayedLoading } from '../../ui/useDelayedLoading';
import AppLoadError from '../../components/AppLoadError.vue';
import { useModalState } from '../../ui/useModalState';
import AppLoading from '../../components/AppLoading.vue';
import AppNumberInput from '../../components/AppNumberInput.vue';
import { NFormItem, NEmpty, NAlert, NBreadcrumb, NBreadcrumbItem, NButton, NInput, NCheckbox, NCard, NCollapseTransition, NPagination, NProgress, NScrollbar, NSpace, NSpin, NText, type ScrollbarInst } from 'naive-ui';
import { useId, computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import AppModal from '../../components/AppModal.vue';
import AppIcon from '../../components/AppIcon.vue';
import AppSelect from '../../components/AppSelect.vue';
import { readApiError, apiFetch, isAbortError, userErrorMessage } from '../../api/client';
import { showToast } from '../../ui/toast';
import { safeModeEnabled } from '../security/safeMode';
import type { Entry, DirectoryResult } from './fileModels';
import { useFileSelection } from './useFileSelection';

const formId = useId();

type Root = { path: string; name: string; kind: string };
type CreateKind = 'folder' | 'file';
type TransferKind = 'copy' | 'move';
type Breadcrumb = { name: string; path: string };
type UploadItem = {
  fileName: string;
  status: 'waiting' | 'preparing' | 'uploading' | 'saving' | 'success' | 'failed';
  percentage: number;
  error?: string;
};
type EditResponse = {
  path: string;
  content: string;
  length: number;
  lastWriteTime: string;
  encoding: string;
};

const roots = ref<Root[]>([]),
  directory = ref<DirectoryResult | undefined>(),
  rootPickerOpen = ref(true),
  pageError = ref('');
const fileSelection = useFileSelection(directory);
const {
  fileSearch, page, pageSize, selectedEntries, filteredEntries, pageCount, pagedEntries,
  selectedPaths, allPagedEntriesSelected, somePagedEntriesSelected,
  normalizePageSize, toggleEntrySelection, togglePagedSelection,
} = fileSelection;
const requestedPath = ref<string>();
const rootsLoaded = ref(false);
const createKindModal = useModalState<CreateKind | undefined>(() => undefined);
const entryMenuModal = useModalState<Entry | undefined>(() => undefined);
const renameTargetModal = useModalState<Entry | undefined>(() => undefined);
const deleteTargetsModal = useModalState<Entry[] | undefined>(() => undefined);
const transferKindModal = useModalState<TransferKind | undefined>(() => undefined);
const uploadItemsModal = useModalState<UploadItem[]>(() => [], value => value.length > 0);
const uploadWarningModal = useModalState(() => false);
const createKind = createKindModal.data,
  newName = ref(''),
  entryMenu = entryMenuModal.data,
  renameTarget = renameTargetModal.data,
  renameName = ref(''),
  deleteTargets = deleteTargetsModal.data,
  transferKind = transferKindModal.data,
  transferTargets = ref<Entry[]>([]),
  targetDirectory = ref<DirectoryResult | undefined>(),
  targetPath = ref(''),
  uploadInput = ref<HTMLInputElement>(),
  uploadFiles = ref<File[]>([]),
  uploadDirectoryPath = ref(''),
  uploadItems = uploadItemsModal.data,
  uploadWarning = uploadWarningModal.data;
const downloadTargetsModal = useModalState<Entry[] | undefined>(() => undefined);
const editTargetModal = useModalState<Entry | undefined>(() => undefined);
const editSaveConfirmModal = useModalState(() => false);
const downloadTargets = downloadTargetsModal.data,
  downloadWarning = ref(false),
  editTarget = editTargetModal.data,
  editContent = ref(''),
  editOriginalLength = ref(0),
  editOriginalLastWriteTime = ref(''),
  editEncoding = ref('utf-8'),
  editSaveConfirm = editSaveConfirmModal.data,
  editLineScrollTop = ref(0);
let isPageActive = true;
const requests = reactive(new Map<string, AbortController>());
const loading = computed(() => requests.has('directory'));
const rootsLoading = computed(() => requests.has('roots'));
const submitting = computed(() => requests.has('write'));
const editLoading = computed(() => requests.has('editRead'));
const editLoaded = ref(false);
const uploading = computed(() => requests.has('upload'));
const downloading = computed(() => requests.has('download'));
const targetLoading = computed(() => requests.has('targetDirectory'));

function cancelRequests() {
  for (const controller of requests.values()) controller.abort();
  requests.clear();
}
function ensureRequestActive(signal: AbortSignal) {
  if (!isPageActive || safeModeEnabled.value || signal.aborted)
    throw new DOMException('请求已取消', 'AbortError');
}
// 按操作类型隔离请求；读取可取代旧请求，写入通过 replace=false 阻止同类操作重复提交。
async function runRequest<T>(
  key: string,
  action: (signal: AbortSignal) => Promise<T>,
  fallback: string,
  replace = true,
): Promise<T | undefined> {
  if (!isPageActive || safeModeEnabled.value) return;
  const previous = requests.get(key);
  if (previous && !replace) return;
  previous?.abort();
  const controller = new AbortController();
  requests.set(key, controller);
  try {
    const result = await action(controller.signal);
    ensureRequestActive(controller.signal);
    return result;
  } catch (exception) {
    if (
      isPageActive &&
      !controller.signal.aborted &&
      requests.get(key) === controller &&
      !isAbortError(exception)
    ) {
      const message = userErrorMessage(exception, fallback);
      if (key === 'roots' || key === 'directory') pageError.value = message;
      else showToast(message);
    }
  } finally {
    if (requests.get(key) === controller) requests.delete(key);
  }
}
async function fetchResponse(
  url: string,
  signal: AbortSignal,
  init?: RequestInit,
) {
  const response = await apiFetch(url, { ...init, signal });
  ensureRequestActive(signal);
  if (!response.ok) throw new Error(await readApiError(response, '文件操作失败'));
  return response;
}
async function readJson<T>(url: string, signal: AbortSignal) {
  const response = await fetchResponse(url, signal);
  const result = (await response.json()) as T;
  ensureRequestActive(signal);
  return result;
}

const editLineNumbers = computed(() =>
  Array.from(
    { length: Math.max(1, editContent.value.split('\n').length) },
    (_, index) => index + 1,
  ),
);
const deleteHasSensitivePath = computed(
  () => deleteTargets.value?.some((entry) => entry.isSensitiveSystemPath) ?? false,
);
const renameHasSensitivePath = computed(() => renameTarget.value?.isSensitiveSystemPath ?? false);
const moveHasSensitivePath = computed(
  () =>
    transferKind.value === 'move' &&
    transferTargets.value.some((entry) => entry.isSensitiveSystemPath),
);
const breadcrumbs = computed<Breadcrumb[]>(() => {
  const directoryPath = directory.value?.path;
  if (!directoryPath) return [];

  const match = /^([A-Za-z]:)\\?(.*)$/.exec(directoryPath);
  if (!match) return [{ name: directoryPath, path: directoryPath }];

  const [, drive, relativePath] = match;
  const result = [{ name: drive, path: `${drive}\\` }];
  let currentPath = `${drive}\\`;
  for (const segment of relativePath.split('\\').filter(Boolean)) {
    currentPath += segment;
    result.push({ name: segment, path: currentPath });
    currentPath += '\\';
  }
  return result;
});
const breadcrumbScrollbar = ref<ScrollbarInst | null>(null);
const breadcrumbContent = ref<HTMLElement | null>(null);
const targetPathScrollbar = ref<ScrollbarInst | null>(null);
const targetPathContent = ref<HTMLElement | null>(null);
watch(
  () => directory.value?.path,
  () => {
    if (breadcrumbContent.value) breadcrumbScrollbar.value?.scrollTo({ left: breadcrumbContent.value.scrollWidth });
  },
  { flush: 'post' },
);
watch(
  () => targetDirectory.value?.path,
  () => {
    if (targetPathContent.value) targetPathScrollbar.value?.scrollTo({ left: targetPathContent.value.scrollWidth });
  },
  { flush: 'post' },
);
onMounted(() => {
  if (!safeModeEnabled.value) void loadRoots();
});
watch(
  safeModeEnabled,
  (enabled) => {
    if (!enabled) {
      void loadRoots();
      return;
    }
    cancelRequests();
    roots.value = [];
    requestedPath.value = undefined;
    directory.value = undefined;
    selectedEntries.value = [];
    pageError.value = '';
    rootPickerOpen.value = true;
    createKindModal.reset();
    entryMenuModal.reset();
    renameTargetModal.reset();
    deleteTargetsModal.reset();
    transferKindModal.reset();
    downloadTargetsModal.reset();
    editTargetModal.reset();
    editSaveConfirmModal.reset();
    editContent.value = '';
    uploadWarningModal.reset();
    uploadFiles.value = [];
    uploadDirectoryPath.value = '';
    uploadItemsModal.reset();
  },
  { flush: 'sync' },
);
watch(
  transferKind,
  (kind) => {
    if (!kind) {
      requests.get('targetDirectory')?.abort();
      transferTargets.value = [];
      targetDirectory.value = undefined;
      targetPath.value = '';
    }
  },
  { flush: 'sync' },
);
watch(editTarget, (entry) => {
  if (!entry) {
    editSaveConfirmModal.close();
    editContent.value = '';
  }
});
onBeforeUnmount(() => {
  isPageActive = false;
  cancelRequests();
  uploadFiles.value = [];
  uploadDirectoryPath.value = '';
  uploadItemsModal.reset();
  editContent.value = '';
});

async function loadRoots() {
  await runRequest(
    'roots',
    async (signal) => {
      roots.value = await readJson<Root[]>('/api/files/roots', signal);
      rootsLoaded.value = true;
      pageError.value = '';
    },
    '无法读取驱动器',
  );
}
async function browse(path: string) {
  requestedPath.value = path;
  await runRequest(
    'directory',
    async (signal) => {
      const result = await readJson<DirectoryResult>(
        `/api/files/directory?path=${encodeURIComponent(path)}`,
        signal,
      );
      directory.value = result;
      pageError.value = '';
      selectedEntries.value = [];
      rootPickerOpen.value = false;
      fileSearch.value = '';
      page.value = 1;
    },
    '无法读取文件夹',
  );
}
async function refreshDirectory(path: string | undefined) {
  if (path && directory.value?.path === path && !loading.value) await browse(path);
}
function toggleRootPicker() {
  rootPickerOpen.value = !rootPickerOpen.value;
}
function handleRowClick(entry: Entry) {
  if (entry.isDirectory) void browse(entry.path);
}
function handleNameClick(entry: Entry) {
  entryMenuModal.open(entry);
}
function startCreate() {
  createKindModal.open('folder');
  newName.value = '';
}
function startRename(entry: Entry) {
  renameTargetModal.open(entry);
  renameName.value = entry.name;
}
function startEntryTransfer(kind: TransferKind, entries?: Entry[]) {
  const targets = entries?.length ? entries : entryMenu.value ? [entryMenu.value] : [];
  if (!targets.length) return;
  transferKindModal.open(kind);
  transferTargets.value = targets;
  targetDirectory.value = undefined;
  targetPath.value = '';
  entryMenuModal.close();
}
function resetTargetDirectory() {
  requests.get('targetDirectory')?.abort();
  requests.delete('targetDirectory');
  targetDirectory.value = undefined;
  targetPath.value = '';
}
async function browseTarget(path: string) {
  targetPath.value = '';
  await runRequest(
    'targetDirectory',
    async (signal) => {
      const result = await readJson<DirectoryResult>(
        `/api/files/directory?path=${encodeURIComponent(path)}`,
        signal,
      );
      targetDirectory.value = result;
      targetPath.value = result.path;
    },
    '无法读取目标文件夹',
  );
}
function chooseUpload() {
  if (directory.value && !uploading.value && !safeModeEnabled.value) uploadInput.value?.click();
}
function selectUploadFiles(event: Event) {
  const input = event.target as HTMLInputElement;
  const files = Array.from(input.files ?? []);
  input.value = '';
  if (!files.length || !directory.value || uploading.value || safeModeEnabled.value) return;
  uploadFiles.value = files;
  uploadDirectoryPath.value = directory.value.path;
  if (directory.value.isSensitiveSystemPath) uploadWarningModal.open(true);
  else void uploadSelectedFiles();
}
async function uploadSelectedFiles() {
  const directoryPath = uploadDirectoryPath.value;
  const files = uploadFiles.value;
  if (!directoryPath || !files.length || uploading.value) return;
  uploadWarningModal.close();
  uploadFiles.value = [];
  uploadDirectoryPath.value = '';
  const chunkSize = 512 * 1024;
  const statuses: UploadItem[] = files.map((file) => ({
    fileName: file.name,
    status: 'waiting',
    percentage: 0,
  }));
  function updateUpload(index: number, changes: Partial<Omit<UploadItem, 'fileName'>>) {
    statuses[index] = { ...statuses[index]!, ...changes };
    uploadItems.value = [...statuses];
  }
  uploadItemsModal.open(statuses);
  await runRequest(
    'upload',
    async (signal) => {
      for (const [index, file] of files.entries()) {
        ensureRequestActive(signal);
        updateUpload(index, { status: 'preparing' });
        let uploadId = '';
        try {
          const started = await fetchResponse('/api/files/upload/start', signal, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ directory: directoryPath, fileName: file.name, size: file.size }),
          });
          uploadId = ((await started.json()) as { uploadId: string }).uploadId;
          ensureRequestActive(signal);
          updateUpload(index, { status: 'uploading' });
          for (let offset = 0; offset < file.size; offset += chunkSize) {
            const chunk = file.slice(offset, Math.min(offset + chunkSize, file.size));
            await fetchResponse(
              `/api/files/upload/chunk?uploadId=${encodeURIComponent(uploadId)}&offset=${offset}`,
              signal,
              { method: 'POST', headers: { 'Content-Type': 'application/octet-stream' }, body: chunk },
            );
            updateUpload(index, {
              percentage: Math.min(100, (offset + chunk.size) / file.size * 100),
            });
          }
          updateUpload(index, { status: 'saving' });
          await fetchResponse('/api/files/upload/complete', signal, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ uploadId }),
          });
          updateUpload(index, { status: 'success', percentage: 100 });
        } catch (exception) {
          try {
            ensureRequestActive(signal);
            updateUpload(index, {
              status: 'failed',
              error: userErrorMessage(exception, '文件上传失败'),
            });
          } finally {
            if (uploadId) {
              await apiFetch('/api/files/upload/cancel', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ uploadId }),
                signal: AbortSignal.timeout(5000),
              }).catch(() => undefined);
            }
          }
        }
      }
      await refreshDirectory(directoryPath);
    },
    '文件上传失败',
    false,
  );
}
function requestDownload(entries: Entry[]) {
  downloadTargetsModal.open(entries);
  downloadWarning.value = entries.some((entry) => entry.isSensitiveSystemPath);
}
async function confirmDownload() {
  const entries = downloadTargets.value;
  if (!entries?.length || downloading.value) return;
  downloadWarning.value = false;
  downloadTargetsModal.close();
  await runRequest(
    'download',
    async (signal) => {
      const response = await fetchResponse('/api/files/download', signal, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ paths: entries.map((entry) => entry.path) }),
      });
      const blob = await response.blob();
      ensureRequestActive(signal);
      const entry = entries[0];
      const name =
        entries.length === 1
          ? entry.isDirectory
            ? `${entry.name}.zip`
            : entry.name
          : 'Download.zip';
      const url = URL.createObjectURL(blob);
      try {
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = name;
        document.body.append(anchor);
        try {
          anchor.click();
        } finally {
          anchor.remove();
        }
      } finally {
        URL.revokeObjectURL(url);
      }
      showToast('下载已开始', 'success');
    },
    '文件下载失败',
    false,
  );
}
async function submitFileOperation(url: string, body: unknown, success: string) {
  const directoryPath = directory.value?.path;
  return (
    (await runRequest(
      'write',
      async (signal) => {
        await fetchResponse(url, signal, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(body),
        });
        showToast(success, 'success');
        await refreshDirectory(directoryPath);
        return true;
      },
      '文件操作失败',
      false,
    )) ?? false
  );
}
async function confirmCreate() {
  if (!createKind.value || !directory.value) return;
  const success = await submitFileOperation(
    `/api/files/${createKind.value}`,
    { parentPath: directory.value.path, name: newName.value.trim() },
    createKind.value === 'folder' ? '已新建文件夹' : '已新建文件',
  );
  if (success) createKindModal.close();
}
async function confirmRename() {
  if (!renameTarget.value) return;
  const success = await submitFileOperation(
    '/api/files/rename',
    { path: renameTarget.value.path, name: renameName.value.trim() },
    '已重命名',
  );
  if (success) renameTargetModal.close();
}
async function confirmDelete() {
  if (!deleteTargets.value) return;
  const success = await submitFileOperation(
    '/api/files/delete',
    { paths: deleteTargets.value.map((entry) => entry.path) },
    `已将 ${deleteTargets.value.length} 项移入回收站`,
  );
  if (success) deleteTargetsModal.close();
}
async function confirmTransfer() {
  if (!transferKind.value || !targetPath.value) return;
  const success = await submitFileOperation(
    `/api/files/${transferKind.value}`,
    {
      sourcePaths: transferTargets.value.map((entry) => entry.path),
      destinationDirectory: targetPath.value,
    },
    transferKind.value === 'copy' ? '已复制' : '已移动',
  );
  if (success) transferKindModal.close();
}
async function startEdit(entry: Entry) {
  if (!isPageActive || safeModeEnabled.value || submitting.value || editLoading.value) return;
  requests.get('editRead')?.abort();
  requests.delete('editRead');
  editLineScrollTop.value = 0;
  editLoaded.value = false;
  editContent.value = '';
  await loadEdit(entry);
}
async function loadEdit(entry = editTarget.value) {
  if (!entry || submitting.value || editLoading.value) return;
  await runRequest(
    'editRead',
    async (signal) => {
      const result = await readJson<EditResponse>(
        `/api/files/edit?path=${encodeURIComponent(entry.path)}`,
        signal,
      );
      editContent.value = result.content;
      editOriginalLength.value = result.length;
      editOriginalLastWriteTime.value = result.lastWriteTime;
      editEncoding.value = result.encoding;
      editLoaded.value = true;
      entryMenuModal.close();
      editTargetModal.open(entry);
    },
    '无法读取文件',
  );
}
function closeEdit() {
  if (submitting.value) return;
  requests.get('editRead')?.abort();
  requests.delete('editRead');
  editTargetModal.close();
}
function requestEditSave() {
  if (editTarget.value && editLoaded.value && !submitting.value) editSaveConfirmModal.open(true);
}
async function confirmEditSave() {
  const entry = editTarget.value;
  if (!entry || !editLoaded.value || submitting.value) return;
  const body = {
    path: entry.path,
    content: editContent.value,
    originalLength: editOriginalLength.value,
    originalLastWriteTime: editOriginalLastWriteTime.value,
    encoding: editEncoding.value,
  };
  editSaveConfirmModal.close();
  await runRequest(
    'write',
    async (signal) => {
      await fetchResponse('/api/files/edit', signal, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      try {
        const result = await readJson<EditResponse>(
          `/api/files/edit?path=${encodeURIComponent(entry.path)}`,
          signal,
        );
        if (editTarget.value === entry) {
          editOriginalLength.value = result.length;
          editOriginalLastWriteTime.value = result.lastWriteTime;
        }
      } catch {
        ensureRequestActive(signal);
        if (editTarget.value === entry) editLoaded.value = false;
        throw new Error('文件已保存，但无法更新文件信息，请重新打开文件后继续编辑');
      }
      showToast('文件已保存', 'success');
    },
    '文件保存失败',
    false,
  );
}
function syncEditLines(event: Event) {
  editLineScrollTop.value = (event.target as HTMLTextAreaElement).scrollTop;
}
function formatSize(value: number | null) {
  if (value === null) return '—';
  if (value < 1024) return `${value} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let size = value / 1024;
  let index = 0;
  while (size >= 1024 && index < units.length - 1) {
    size /= 1024;
    index++;
  }
  return `${size.toFixed(size >= 10 || index === 0 ? 0 : 1)} ${units[index]}`;
}
function formatTime(value: string) {
  return new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
  }).format(
    new Date(value),
  );
}
const showRefreshLoading = useDelayedLoading(() => loading.value);
const showEditLoading = useDelayedLoading(() => editLoading.value);
const showTargetLoading = useDelayedLoading(() => targetLoading.value);
</script>

<template>
  <section
    class="files-view load-error-layout"
    :class="{ 'safe-mode-locked': safeModeEnabled }"
    :inert="safeModeEnabled"
  >
    <AppLoadError :show="Boolean(pageError)" :retry="() => requestedPath ? browse(requestedPath) : loadRoots()">{{ pageError }}</AppLoadError>
    <NCard tag="section" content-class="files-panel">
      <input
        ref="uploadInput"
        class="file-upload-input"
        type="file"
        multiple
        @change="selectUploadFiles"
      />
      <header class="files-header">
        <div class="files-location">
          <NButton
            class="files-crumb"
            attr-type="button"
            :aria-expanded="rootPickerOpen"
            @click="toggleRootPicker"
          >
            选择驱动器
            <AppIcon name="chevron-down" class="files-crumb-chevron" />
          </NButton>
          <NScrollbar v-if="directory" ref="breadcrumbScrollbar" x-scrollable class="files-breadcrumb-scrollbar">
            <div ref="breadcrumbContent" class="files-breadcrumb-content">
              <NBreadcrumb class="files-breadcrumb" aria-label="当前路径">
                <NBreadcrumbItem
                  v-for="(crumb, index) in breadcrumbs"
                  :key="crumb.path"
                  :href="index < breadcrumbs.length - 1 ? '#' : undefined"
                  :clickable="index < breadcrumbs.length - 1"
                  @click.prevent="index < breadcrumbs.length - 1 && browse(crumb.path)"
                >
                  <span :aria-current="index === breadcrumbs.length - 1 ? 'page' : undefined">{{ crumb.name }}</span>
                </NBreadcrumbItem>
              </NBreadcrumb>
            </div>
          </NScrollbar>
          <p v-else>选择驱动器开始浏览</p>
        </div>
        <div class="files-header-actions">
          <NButton
            v-if="directory?.parentPath"
            attr-type="button"
            @click="browse(directory!.parentPath!)"
          >
            后退
          </NButton>
          <NButton
            v-if="directory"
            class="files-refresh"
            :loading="showRefreshLoading"
            :aria-busy="loading"
            aria-label="刷新"
            attr-type="button"
            :disabled="loading && !showRefreshLoading"
            @click="browse(directory!.path)"
          >
            刷新
          </NButton>
        </div>
      </header>
      <NCollapseTransition :show="rootPickerOpen" :inert="!rootPickerOpen || undefined">
        <div class="file-root-list">
          <div
            v-if="rootsLoading && !rootsLoaded && !pageError"
            class="file-roots-loading"
            role="status"
            aria-label="正在加载驱动器"
            aria-busy="true"
          >
            <NSpin size="small">
              <template #description>正在加载驱动器</template>
            </NSpin>
          </div>
          <NEmpty v-if="pageError && !roots.length" :show-icon="false" description="无法加载驱动器列表" class="file-roots-empty" />
          <NButton
            v-for="root in roots"
            :key="root.path"
            class="file-root"
            attr-type="button"
            @click="browse(root.path)"
          >
            <span class="file-root-content">
              <strong>{{ root.name }}</strong>
              <small>{{ root.path }} · {{ root.kind }}</small>
            </span>
          </NButton>
        </div>
      </NCollapseTransition>
      <AppLoading v-if="loading && !directory && !pageError" label="正在读取目录" />
      <template v-if="directory">
        <div class="files-toolbar">
          <label class="form-field files-search">
            <NInput
              v-model:value="fileSearch"
              clearable
              placeholder="按文件名搜索"
              aria-label="当前目录搜索"
              :input-props="{ role: 'searchbox', enterkeyhint: 'search', 'aria-label': '当前目录搜索' }"
            />
          </label>
          <div class="files-toolbar-actions">
            <template v-if="selectedEntries.length">
              <NButton
                attr-type="button"
                :loading="downloading"
                :disabled="downloading || downloadTargetsModal.show"
                @click="requestDownload([...selectedEntries])"
              >
                下载
              </NButton>
              <NButton
                attr-type="button"
                @click="startEntryTransfer('copy', [...selectedEntries])"
              >
                复制
              </NButton>
              <NButton
                attr-type="button"
                @click="startEntryTransfer('move', [...selectedEntries])"
              >
                移动
              </NButton>
              <NButton
                attr-type="button"
                @click="deleteTargetsModal.open([...selectedEntries])"
                type="error"
              >
                删除
              </NButton>
            </template>
            <NButton attr-type="button" :disabled="uploading" @click="chooseUpload">上传</NButton>
            <NButton attr-type="button" @click="startCreate" type="primary">新建</NButton>
          </div>
        </div>
        <div class="file-list">
          <div class="file-list-head">
            <span class="file-select-cell">
              <NCheckbox
                :checked="allPagedEntriesSelected"
                :indeterminate="somePagedEntriesSelected"
                aria-label="选择当前页文件"
                @click.stop
                @update:checked="togglePagedSelection"
              />
            </span>
            <span>名称</span>
            <span>类型</span>
            <span>大小</span>
            <span>修改时间</span>
          </div>
          <article
            v-for="entry in pagedEntries"
            :key="entry.path"
            class="file-row"
            :class="{ 'is-browsable': entry.isDirectory }"
            @click="handleRowClick(entry)"
          >
            <label class="file-select-cell" @click.stop>
              <NCheckbox
                :checked="selectedPaths.has(entry.path)"
                :aria-label="`选择 ${entry.name}`"
                @update:checked="toggleEntrySelection(entry, $event)"
              />
            </label>
            <NButton class="file-name" text attr-type="button" @click.stop="handleNameClick(entry)">
              <span class="file-name-copy">
                <strong>{{ entry.name }}</strong>
                <small v-if="entry.isReparsePoint">重解析点</small>
              </span>
            </NButton>
            <span class="file-type">{{ entry.isDirectory ? '目录' : '文件' }}</span>
            <span class="file-size">{{ formatSize(entry.size) }}</span>
            <time class="file-time">{{ formatTime(entry.lastWriteTime) }}</time>
          </article>
          <NEmpty :show-icon="false" v-if="!pagedEntries.length" class="content-empty">
            {{ filteredEntries.length ? '当前页没有文件' : '当前目录没有符合条件的文件' }}
          </NEmpty>
        </div>
        <div v-if="filteredEntries.length" class="app-pagination">
          <NFormItem
            label="每页"
            :label-props="{ for: `${formId}-field-0` }"
            label-placement="left"
            :show-feedback="false"
            class="form-field page-size-control"
          >
            <AppNumberInput
              :id="`${formId}-field-0`"
              v-model="pageSize"
              min="1"
              step="1"
              inputmode="numeric"
              aria-label="每页项目数"
              @change="normalizePageSize"
            />
          </NFormItem>
          <NPagination v-model:page="page" :page-count="pageCount" simple aria-label="文件分页" />
        </div>
      </template>
    </NCard>
    <AppModal
      :show="uploadWarningModal.show"
      @after-leave="uploadWarningModal.afterLeave"
      v-if="uploadWarning"
      title="确认上传"
      @close="
        uploadWarningModal.close();
        uploadFiles = [];
      "
    >
      <NAlert type="error" class="file-sensitive-warning">
        当前目录位于 Windows
        系统目录或重要程序目录中，上传文件可能影响系统或相关程序。确定要继续吗？
      </NAlert>
      <template #actions>
        <NButton
          attr-type="button"
          @click="
            uploadWarningModal.close();
            uploadFiles = [];
          "
        >
          取消
        </NButton>
        <NButton attr-type="button" @click="uploadSelectedFiles" type="primary">继续上传</NButton>
      </template>
    </AppModal>
    <AppModal
      :show="uploadItemsModal.show"
      @after-leave="uploadItemsModal.afterLeave"
      v-if="uploadItems.length"
      title="上传状态"
      :dismissible="!uploading"
      @close="uploadItemsModal.close()"
    >
      <NText v-if="uploading" tag="p" :depth="3" class="file-upload-notice">
        正在上传中，离开或刷新页面将中断上传
      </NText>
      <div class="file-upload-status">
        <div v-for="(item, index) in uploadItems" :key="index" class="file-upload-item">
          <strong>{{ item.fileName }}</strong>
          <NProgress
            v-if="item.status !== 'waiting' && item.status !== 'preparing'"
            type="line"
            :percentage="item.percentage"
            :status="item.status === 'failed' ? 'error' : item.status === 'success' ? 'success' : 'default'"
            :processing="item.status === 'uploading' || item.status === 'saving'"
            :show-indicator="false"
            :aria-label="`${item.fileName} 上传进度`"
          />
          <NText v-if="item.status === 'waiting'" :depth="3">等待上传</NText>
          <NText v-else-if="item.status === 'preparing'" :depth="3">正在准备…</NText>
          <NText v-else-if="item.status === 'uploading'" :depth="3">上传中 {{ Math.floor(item.percentage) }}%</NText>
          <NText v-else-if="item.status === 'saving'" :depth="3">100% · 正在保存…</NText>
          <NText v-else-if="item.status === 'success'" type="success">上传成功</NText>
          <NText v-else type="error">上传失败{{ item.error ? `：${item.error}` : '' }}</NText>
        </div>
      </div>
    </AppModal>
    <AppModal
      :show="createKindModal.show"
      @after-leave="createKindModal.afterLeave"
      v-if="createKind"
      :dismissible="!submitting"
      title="新建"
      @close="createKindModal.close()"
    >
      <NSpace vertical :size="16">
        <NFormItem
          label="类型"
          :label-props="{ id: (`${formId}-field-1`) + '-label' }"
          :show-feedback="false"
          class="form-field"
        >
          <AppSelect
            :labelled-by="(`${formId}-field-1`) + '-label'"
            :id="`${formId}-field-1`"
            v-model="createKind"
            :disabled="submitting"
            :options="[
              { value: 'folder', label: '文件夹' },
              { value: 'file', label: '文件' },
            ]"
          />
        </NFormItem>
        <NFormItem
          label="名称"
          :label-props="{ for: `${formId}-field-2` }"
          :show-feedback="false"
          class="form-field"
        >
          <NInput
            :input-props="{ id: `${formId}-field-2` }"
            v-model:value="newName"
            :disabled="submitting"
            autofocus
            :placeholder="createKind === 'folder' ? '例如 新建文件夹' : '例如 notes.txt'"
            @keyup.enter="confirmCreate"
          />
        </NFormItem>
      </NSpace>
      <template #actions>
        <NButton
          attr-type="button"
          :disabled="submitting"
          @click="createKindModal.close()"
        >取消</NButton
        >
        <NButton
          :loading="submitting"
          attr-type="button"
          :disabled="submitting"
          @click="confirmCreate"
          type="primary"
        >
          {{ submitting ? '正在创建' : '创建' }}
        </NButton>
      </template>
    </AppModal>
    <AppModal
      :show="entryMenuModal.show"
      @after-leave="entryMenuModal.afterLeave"
      v-if="entryMenu"
      class="file-entry-menu"
      :dismissible="!editLoading"
      :title="entryMenu.name"
      @close="!editLoading && entryMenuModal.close()"
    >
      <p>
        {{ entryMenu.isDirectory ? '选择要对这个文件夹执行的操作' : '选择要对这个文件执行的操作' }}
      </p>
      <template #actions>
        <NButton
          v-if="entryMenu.isDirectory"
          attr-type="button"
          @click="
            browse(entryMenu!.path);
            entryMenuModal.close();
          "
          type="primary"
        >
          进入
        </NButton>
        <NButton
          v-else
          attr-type="button"
          :loading="showEditLoading"
          :aria-busy="editLoading"
          :disabled="editLoading && !showEditLoading"
          @click="startEdit(entryMenu!)"
          type="primary"
        >
          编辑
        </NButton>
        <NButton
          attr-type="button"
          :disabled="editLoading"
          @click="
            requestDownload([entryMenu!]);
            entryMenuModal.close();
          "
        >
          下载
        </NButton>
        <NButton
          attr-type="button"
          :disabled="editLoading"
          @click="
            startRename(entryMenu!);
            entryMenuModal.close();
          "
        >
          重命名
        </NButton>
        <NButton attr-type="button" :disabled="editLoading" @click="startEntryTransfer('copy')"> 复制 </NButton>
        <NButton attr-type="button" :disabled="editLoading" @click="startEntryTransfer('move')"> 移动 </NButton>
        <NButton
          attr-type="button"
          @click="
            deleteTargetsModal.open([entryMenu!]);
            entryMenuModal.close();
          "
          type="error"
          :disabled="editLoading"
        >
          删除
        </NButton>
      </template>
    </AppModal>
    <AppModal
      :show="renameTargetModal.show"
      @after-leave="renameTargetModal.afterLeave"
      v-if="renameTarget"
      :dismissible="!submitting"
      title="重命名"
      @close="renameTargetModal.close()"
    >
      <NAlert type="error" v-if="renameHasSensitivePath" class="file-sensitive-warning">
        此项目位于 Windows
        系统目录或重要程序目录中，重命名可能导致系统或相关程序无法正常运行。确定要继续吗？
      </NAlert>
      <NFormItem
        label="新名称"
        :label-props="{ for: `${formId}-field-3` }"
        :show-feedback="false"
        class="form-field"
      >
        <NInput
          :input-props="{ id: `${formId}-field-3` }"
          v-model:value="renameName"
          :disabled="submitting"
          autofocus
          @keyup.enter="confirmRename"
        />
      </NFormItem>
      <template #actions>
        <NButton
          attr-type="button"
          :disabled="submitting"
          @click="renameTargetModal.close()"
        >取消</NButton
        >
        <NButton
          :loading="submitting"
          attr-type="button"
          :disabled="submitting"
          @click="confirmRename"
          type="primary"
        >
          {{ submitting ? '正在保存' : '保存' }}
        </NButton>
      </template>
    </AppModal>
    <AppModal
      :show="deleteTargetsModal.show"
      @after-leave="deleteTargetsModal.afterLeave"
      v-if="deleteTargets"
      :dismissible="!submitting"
      title="确认移入回收站"
      @close="deleteTargetsModal.close()"
    >
      <p>将把 {{ deleteTargets.length }} 项移入 Windows 回收站，确认继续吗？</p>
      <NAlert type="error" v-if="deleteHasSensitivePath" class="file-sensitive-warning">
        其中包含 Windows
        系统目录或重要程序目录中的项目，删除可能导致系统或相关程序无法正常运行。确定要继续吗？
      </NAlert>
      <template #actions>
        <NButton
          attr-type="button"
          :disabled="submitting"
          @click="deleteTargetsModal.close()"
        >取消</NButton
        >
        <NButton
          :loading="submitting"
          attr-type="button"
          :disabled="submitting"
          @click="confirmDelete"
          type="error"
        >
          {{ submitting ? '正在操作' : '移入回收站' }}
        </NButton>
      </template>
    </AppModal>
    <AppModal
      :show="transferKindModal.show"
      @after-leave="transferKindModal.afterLeave"
      v-if="transferKind"
      :dismissible="!submitting"
      :title="transferKind === 'copy' ? '复制到文件夹' : '移动到文件夹'"
      @close="transferKindModal.close()"
    >
      <NSpin :show="showTargetLoading" size="small">
        <div class="file-target-picker" :inert="submitting || targetLoading" :aria-busy="targetLoading">
          <p>已选择 {{ transferTargets.length }} 项。请选择目标文件夹：</p>
          <NAlert type="error" v-if="moveHasSensitivePath" class="file-sensitive-warning">
            其中包含 Windows
            系统目录或重要程序目录中的项目，移动可能导致系统或相关程序无法正常运行。确定要继续吗？
          </NAlert>
          <div v-if="!targetDirectory" class="file-root-list">
            <NButton
              v-for="root in roots"
              :key="root.path"
              class="file-root"
              attr-type="button"
              @click="browseTarget(root.path)"
            >
              <span class="file-root-content">
                <strong>{{ root.name }}</strong>
                <small>{{ root.path }}</small>
              </span>
            </NButton>
          </div>
          <template v-else>
            <NScrollbar ref="targetPathScrollbar" x-scrollable class="file-target-path">
              <span ref="targetPathContent" class="file-target-path-text">{{ targetDirectory.path }}</span>
            </NScrollbar>
            <div class="file-target-actions">
              <NButton
                v-if="targetDirectory.parentPath"
                attr-type="button"
                @click="browseTarget(targetDirectory!.parentPath!)"
              >
                返回上级
              </NButton>
              <NButton attr-type="button" @click="resetTargetDirectory()"> 选择其他根目录 </NButton>
            </div>
            <NScrollbar class="file-target-scrollbar" content-class="file-target-list">
              <NButton
                v-for="entry in targetDirectory.entries.filter((item) => item.isDirectory)"
                :key="entry.path"
                class="file-target-button"
                attr-type="button"
                quaternary
                @click="browseTarget(entry.path)"
              >
                {{ entry.name }}
                <small v-if="entry.isReparsePoint">重解析点</small>
              </NButton>
              <NEmpty
                :show-icon="false"
                v-if="!targetDirectory.entries.some((item) => item.isDirectory)"
                class="file-target-empty"
              >
                没有可进入的子文件夹
              </NEmpty>
            </NScrollbar>
          </template>
        </div>
      </NSpin>
      <template #actions>
        <NButton
          attr-type="button"
          :disabled="submitting"
          @click="transferKindModal.close()"
        >取消</NButton
        >
        <NButton
          :loading="submitting"
          attr-type="button"
          :disabled="submitting || !targetPath || targetLoading"
          @click="confirmTransfer"
          type="primary"
        >
          {{
            submitting
              ? transferKind === 'copy'
                ? '正在复制'
                : '正在移动'
              : transferKind === 'copy'
                ? '复制到此处'
                : '移动到此处'
          }}
        </NButton>
      </template>
    </AppModal>
  </section>
  <AppModal
    :show="downloadTargetsModal.show"
    @after-leave="downloadTargetsModal.afterLeave"
    v-if="downloadTargets"
    title="确认下载"
    @close="
      downloadTargetsModal.close();
      downloadWarning = false;
    "
  >
    <NAlert type="error" v-if="downloadWarning" class="file-sensitive-warning">
      其中包含 Windows 系统目录或重要程序目录中的项目，下载操作可能触发系统权限限制。确定要继续吗？
    </NAlert>
    <p v-else>将下载所选项目，是否继续？</p>
    <template #actions>
      <NButton
        attr-type="button"
        @click="
          downloadTargetsModal.close();
          downloadWarning = false;
        "
      >
        取消
      </NButton>
      <NButton attr-type="button" @click="confirmDownload" type="primary">继续下载</NButton>
    </template>
  </AppModal>
  <AppModal
    :show="editTargetModal.show"
    @after-leave="editTargetModal.afterLeave"
    v-if="editTarget"
    :dismissible="!submitting"
    class="file-edit-modal"
    :title="`编辑：${editTarget.name}`"
    :dismiss-on-backdrop="false"
    @close="closeEdit"
  >
    <div class="file-editor">
      <div class="file-editor-lines" aria-hidden="true">
        <div
          class="file-editor-line-content"
          :style="{ transform: `translateY(-${editLineScrollTop}px)` }"
        >
          <span v-for="line in editLineNumbers" :key="line">{{ line }}</span>
        </div>
      </div>
      <textarea
        v-model="editContent"
        :readonly="submitting || !editLoaded"
        aria-label="文件内容"
        wrap="off"
        spellcheck="false"
        @scroll="syncEditLines"
      ></textarea>
    </div>
    <template #actions>
      <NButton
        attr-type="button"
        :disabled="submitting"
        @click="closeEdit"
      >关闭</NButton
      >
      <NButton
        attr-type="button"
        :loading="submitting"
        :disabled="!editLoaded || submitting || editSaveConfirmModal.show"
        @click="requestEditSave"
        type="primary"
      >保存</NButton
      >
    </template>
  </AppModal>
  <AppModal
    :show="editSaveConfirmModal.show"
    @after-leave="editSaveConfirmModal.afterLeave"
    v-if="editSaveConfirm"
    title="确认保存"
    @close="editSaveConfirmModal.close()"
  >
    <p>确定保存对「{{ editTarget?.name }}」的修改吗？</p>
    <template #actions>
      <NButton attr-type="button" @click="editSaveConfirmModal.close()">取消</NButton>
      <NButton
        :loading="submitting"
        attr-type="button"
        :disabled="submitting"
        @click="confirmEditSave"
        type="primary"
      >{{ submitting ? '正在保存' : '保存' }}</NButton
      >
    </template>
  </AppModal>
</template>

<style>
.files-view {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  width: 100%;
  max-width: none;
  gap: var(--page-card-row-gap) 18px;
}
.files-panel {
  --file-list-columns: 30px minmax(180px, 1fr) minmax(80px, 0.35fr) 88px 132px;
  min-width: 0;
}
.files-header, .files-breadcrumb, .file-list, .file-target-scrollbar {
  transition: border-color .3s cubic-bezier(.4, 0, .2, 1);
}
.files-header p, .file-root small, .file-list-head span, .file-target-button small, .file-row small, .file-type, .file-size, .file-time {
  transition: color .3s cubic-bezier(.4, 0, .2, 1);
}
.files-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  min-height: 54px;
  gap: 16px;
  padding-bottom: 14px;
  border-bottom: 1px solid var(--border);
}
.files-header p {
  margin: 6px 0 0;
  color: var(--text-muted);
  font-size: 13px;
}
.files-location {
  display: flex;
  flex: 1;
  align-items: center;
  min-width: 0;
  gap: 10px;
}
.files-header-actions, .files-toolbar-actions, .file-target-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.files-header-actions {
  flex-shrink: 0;
  align-items: center;
}
.files-crumb { flex-shrink: 0; width: fit-content; }
.files-crumb-chevron { flex-shrink: 0; font-size: 16px; transition: transform 0.15s ease; }
.files-crumb[aria-expanded='true'] .files-crumb-chevron { transform: rotate(180deg); }
.files-breadcrumb-scrollbar { flex: 1; width: auto; min-width: 0; }
.files-breadcrumb-content { width: max-content; min-width: 100%; }
.files-breadcrumb { min-width: max-content; padding-block: 5px; white-space: nowrap; }
.file-root-list {
  --file-root-height: 88px;
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 12px;
  margin-top: 14px;
}
.file-roots-empty {
  grid-column: 1 / -1;
  align-items: flex-start;
  text-align: left;
}
.file-roots-loading {
  grid-column: 1 / -1;
  display: grid;
  height: var(--file-root-height);
  place-items: center;
}
.file-root {
  min-height: var(--file-root-height);
  height: auto;
  width: 100%;
  padding: 14px;
  justify-content: flex-start;
  text-align: left;
}
.file-root-content {
  display: grid;
  min-width: 0;
  max-width: 100%;
  gap: 5px;
  line-height: 1.5;
}.file-root small {
  overflow: hidden;
  color: var(--text-muted);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.files-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-top: 16px;
  padding: 2px 0;
}
.files-search {
  width: min(100%, 360px);
}
.files-search input {
  width: 100%;
}
.files-toolbar-actions {
  margin-left: auto;
}
.file-upload-input {
  display: none;
}
.file-upload-status {
  display: grid;
  gap: 16px;
}
.file-upload-notice {
  margin: 0 0 16px;
}
.file-upload-item {
  display: grid;
  min-width: 0;
  gap: 8px;
  overflow-wrap: anywhere;
}
.file-list {
  margin-top: 14px;
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
}
.file-list-head, .file-row {
  display: grid;
  grid-template-columns: var(--file-list-columns);
  align-items: center;
  gap: 7px;
  padding: 11px 12px;
}
.file-list-head, .file-row, .file-target-path, .file-editor, .file-editor-lines, .file-editor textarea {
  transition:
    background-color .3s cubic-bezier(.4, 0, .2, 1),
    color .3s cubic-bezier(.4, 0, .2, 1),
    border-color .3s cubic-bezier(.4, 0, .2, 1);
}
.file-list-head {
  min-height: 50px;
  border-bottom: 1px solid var(--border);
  background: var(--surface-subtle);
  color: var(--text-muted);
  font-size: 12px;
  font-weight: 500;
}
.file-row {
  min-height: 62px;
  border-bottom: 1px solid var(--border);
}
.file-row:last-of-type {
  border-bottom: 0;
}
.file-row.is-browsable {
  cursor: pointer;
}
@media (hover: hover) and (pointer: fine) {
  .file-row.is-browsable:hover {
    background: var(--surface-hover);
  }
}.file-select-cell {
  display: grid;
  min-width: 0;
  place-items: center;
}
.file-name {
  min-width: 0;
  max-width: 100%;
  height: auto;
  justify-self: start;
  padding: 6px 8px 6px 0;
  text-align: left;
}
.file-name-copy {
  display: grid;
  min-width: 0;
  max-width: 100%;
  gap: 3px;
  line-height: 1.5;
}
.file-name strong, .file-type, .file-size, .file-time {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.file-name small {
  color: var(--warning-text);
  font-size: 11px;
}
.file-type, .file-size, .file-time {
  color: var(--text-muted);
  font-size: 13px;
}
.file-target-picker {
  display: grid;
  gap: 12px;
}
.file-target-picker > p {
  margin: 0;
}
.file-target-path {
  min-width: 0;
  border: 1px solid var(--border);
  border-radius: var(--radius-sm);
  background: var(--surface-subtle);
  color: var(--text-muted);
  font-family: var(--font-mono);
  font-size: 12px;
}
.file-target-path-text {
  display: block;
  width: max-content;
  padding: 9px 10px;
  white-space: pre;
}
.file-target-scrollbar {
  max-height: 260px;
  border: 1px solid var(--border);
  border-radius: var(--radius-sm);
}
.file-target-list { display: grid; }
.file-target-button {
  min-height: 42px;
  height: auto;
  justify-content: flex-start;
}
.file-target-button small {
  color: var(--warning-text);
}
.file-target-empty {
  padding: 12px;
}
.file-list > .content-empty {
  margin: 0;
  padding: 14px 12px;
}
.file-sensitive-warning {
  margin: 0 0 12px;
}
.file-edit-modal .modal-content {
  display: flex;
  flex-direction: column;
  min-height: 0;
  flex: 1 1 auto;
  overflow: hidden;
}
.app-modal.file-edit-modal {
  --dialog-width: 1200px;
}
.file-editor {
  display: grid;
  grid-template-columns: 44px minmax(0, 1fr);
  grid-template-rows: minmax(0, 1fr);
  width: 100%;
  height: min(70vh, 760px);
  min-height: 0;
  min-width: 0;
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: var(--radius-sm);
  background: var(--surface-subtle);
  font-family: var(--font-mono);
}
.file-editor-lines {
  min-height: 0;
  overflow: hidden;
  padding: 10px 8px;
  color: var(--text-muted);
  font-size: 12px;
  line-height: 24px;
  user-select: none;
}
.file-editor-line-content {
  display: flex;
  flex-direction: column;
  align-items: end;
}
.file-editor textarea {
  display: block;
  width: 100%;
  height: auto;
  min-width: 0;
  min-height: 0;
  padding: 10px 12px;
  border: 0;
  border-left: 1px solid var(--border);
  border-radius: 0;
  outline: 0;
  background: var(--surface);
  color: var(--text);
  font: inherit;
  font-size: var(--control-font-size);
  line-height: 24px;
  resize: none;
  tab-size: 4;
  white-space: pre;
  overflow: auto;
  overflow-wrap: normal;
  scrollbar-gutter: stable both-edges;
}
@media (max-width: 767px) {
  .files-panel {
    --file-list-columns: 30px minmax(0, 1fr) max-content;
  }
  .files-header {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    align-items: start;
    padding-bottom: 0;
  }
  .files-location {
    display: contents;
  }
  .files-crumb {
    grid-column: 1;
    grid-row: 1;
  }
  .files-breadcrumb-scrollbar, .files-location > p {
    grid-column: 1 / -1;
    grid-row: 2;
  }
  .files-location > p {
    margin: 8px 0 12px;
  }
  .files-breadcrumb {
    padding: 15px 0;
    border-top: 1px solid var(--border);
  }
  .files-header-actions {
    grid-column: 2;
    grid-row: 1;
    align-items: stretch;
  }
  .files-toolbar {
    align-items: stretch;
    flex-direction: column;
    gap: 10px;
    margin-top: 14px;
  }
  .files-search {
    width: 100%;
  }
  .files-toolbar-actions {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    width: 100%;
    min-width: 0;
    margin-left: 0;
  }
  .file-root-list {
    grid-template-columns: 1fr;
    gap: 8px;
    margin-top: 12px;
  }
  .file-root {
    min-height: var(--file-root-height);
    height: auto;
    width: 100%;
    padding: 14px;
    justify-content: flex-start;
    text-align: left;
  }
  .file-root-content {
    display: grid;
    min-width: 0;
    max-width: 100%;
    gap: 5px;
  }
  .file-list-head, .file-row {
    grid-template-columns: var(--file-list-columns);
    padding-right: 8px;
  }
  .file-list-head {
    grid-template-rows: repeat(2, auto);
    gap: 3px 6px;
    padding-block: 8px;
    padding-left: 0;
    border-inline: 0;
    border-radius: 0;
    font-weight: 500;
  }
  .file-list-head span {
    line-height: 1.3;
    text-align: left;
  }
  .file-list-head span:nth-of-type(1) {
    grid-column: 1;
    grid-row: 1 / span 2;
    padding-inline: 0;
  }
  .file-list-head span:nth-of-type(2) {
    grid-column: 2;
    grid-row: 1;
    padding-inline: 4px;
  }
  .file-list-head span:nth-of-type(3) {
    grid-column: 3;
    grid-row: 1;
    padding-inline: 0;
    color: var(--text-muted);
    text-align: right;
  }
  .file-list-head span:nth-of-type(4) {
    grid-column: 2;
    grid-row: 2;
    padding-inline: 4px;
    color: var(--text-muted);
  }
  .file-list-head span:nth-of-type(5) {
    grid-column: 3;
    grid-row: 2;
    padding-inline: 0;
    text-align: right;
  }
  .file-row {
    gap: 6px;
    padding-block: 12px;
    padding-left: 0;
    -webkit-tap-highlight-color: transparent;
  }
  .file-select-cell {
    grid-column: 1;
    grid-row: 1 / span 2;
  }
  .file-name {
    grid-column: 2;
    grid-row: 1;
    padding-left: 4px;
  }
  .file-type {
    grid-column: 3;
    grid-row: 1;
    text-align: right;
  }
  .file-size, .file-time {
    grid-row: 2;
  }
  .file-size {
    grid-column: 2;
    padding-inline: 4px;
  }
  .file-time {
    grid-column: 3;
    margin-left: 0;
    overflow: visible;
    text-overflow: clip;
    text-align: right;
    font-variant-numeric: tabular-nums;
  }
  .file-target-scrollbar {
    max-height: 220px;
  }
  .file-editor {
    height: 58vh;
    grid-template-columns: 34px minmax(0, 1fr);
  }
  .file-editor-lines {
    padding-inline: 5px;
    line-height: 24px;
  }
  .file-editor textarea {
    min-height: 48vh;
    padding-inline: 8px;
  }
  .app-modal.file-edit-modal {
    --dialog-width: 560px;
  }
  .file-entry-menu .modal-actions {
    display: grid;
    grid-template-columns: 1fr;
    gap: 8px;
    padding: 12px 16px 16px;
  }
}
</style>
