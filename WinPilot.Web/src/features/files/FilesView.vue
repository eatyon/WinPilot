<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import AppModal from '../../components/AppModal.vue';
import AppSelect from '../../components/AppSelect.vue';
import { readApiError, apiFetch, isAbortError, userErrorMessage } from '../../api/client';
import { showToast } from '../../ui/toast';
import { safeModeEnabled } from '../security/safeMode';

type Root = { path: string; name: string; kind: string };
type Entry = {
  name: string;
  path: string;
  isDirectory: boolean;
  isReparsePoint: boolean;
  isSensitiveSystemPath: boolean;
  type: string;
  size: number | null;
  lastWriteTime: string;
};
type DirectoryResult = {
  path: string;
  parentPath: string | null;
  entries: Entry[];
  isSensitiveSystemPath: boolean;
};
type CreateKind = 'folder' | 'file';
type TransferKind = 'copy' | 'move';
type Breadcrumb = { name: string; path: string };
type UploadItem = { fileName: string; status: 'uploading' | 'success' | 'failed'; error?: string };
type UploadResponse = { fileName: string; success: boolean; error?: string | null };
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
  fileSearch = ref(''),
  page = ref(1),
  pageSize = ref(25),
  selectedEntries = ref<Entry[]>([]),
  pageError = ref('');
const createKind = ref<CreateKind | undefined>(),
  newName = ref(''),
  entryMenu = ref<Entry | undefined>(),
  renameTarget = ref<Entry | undefined>(),
  renameName = ref(''),
  deleteTargets = ref<Entry[] | undefined>(),
  transferKind = ref<TransferKind | undefined>(),
  transferTargets = ref<Entry[]>([]),
  targetDirectory = ref<DirectoryResult | undefined>(),
  targetPath = ref(''),
  uploadInput = ref<HTMLInputElement>(),
  uploadFiles = ref<File[]>([]),
  uploadDirectoryPath = ref(''),
  uploadItems = ref<UploadItem[]>([]),
  uploadWarning = ref(false);
const downloadTargets = ref<Entry[] | undefined>(),
  downloadWarning = ref(false),
  editTarget = ref<Entry | undefined>(),
  editContent = ref(''),
  editOriginalLength = ref(0),
  editOriginalLastWriteTime = ref(''),
  editEncoding = ref('utf-8'),
  editSaveConfirm = ref(false),
  editLineScrollTop = ref(0);
let isPageActive = true;
const requests = reactive(new Map<string, AbortController>());
const loading = computed(() => requests.has('directory'));
const submitting = computed(() => requests.has('write'));
const editLoading = computed(() => requests.has('editRead'));
const uploading = computed(() => requests.has('upload'));
const downloading = computed(() => requests.has('download'));

function cancelRequests() {
  for (const controller of requests.values()) controller.abort();
  requests.clear();
}
function ensureRequestActive(signal: AbortSignal) {
  if (!isPageActive || safeModeEnabled.value || signal.aborted)
    throw new DOMException('请求已取消', 'AbortError');
}
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
    if (isPageActive && !controller.signal.aborted && requests.get(key) === controller && !isAbortError(exception)) {
      const message = userErrorMessage(exception, fallback);
      if (key === 'roots' || key === 'directory') pageError.value = message;
      showToast(message);
    }
  } finally {
    if (requests.get(key) === controller) requests.delete(key);
  }
}
async function fetchResponse(url: string, signal: AbortSignal, init?: RequestInit) {
  const response = await apiFetch(url, { ...init, signal });
  ensureRequestActive(signal);
  if (!response.ok) throw new Error(await readApiError(response, '文件操作失败'));
  return response;
}
async function readJson<T>(url: string, signal: AbortSignal) {
  const response = await fetchResponse(url, signal);
  const result = await response.json() as T;
  ensureRequestActive(signal);
  return result;
}

const filteredEntries = computed(() => {
  const keyword = fileSearch.value.trim().toLocaleLowerCase();
  return keyword
    ? (directory.value?.entries ?? []).filter((entry) => entry.name.toLocaleLowerCase().includes(keyword))
    : (directory.value?.entries ?? []);
});
const effectivePageSize = computed(() =>
  Number.isFinite(pageSize.value) && pageSize.value > 0 ? Math.floor(pageSize.value) : 25,
);
const pageCount = computed(() =>
  Math.max(1, Math.ceil(filteredEntries.value.length / effectivePageSize.value)),
);
const pagedEntries = computed(() =>
  filteredEntries.value.slice(
    (page.value - 1) * effectivePageSize.value,
    page.value * effectivePageSize.value,
  ),
);
const selectedPageCount = computed(() =>
  pagedEntries.value.filter((entry) => selectedEntries.value.some((selected) => selected.path === entry.path)).length,
);
const allPagedEntriesSelected = computed(
  () => pagedEntries.value.length > 0 && selectedPageCount.value === pagedEntries.value.length,
);
const somePagedEntriesSelected = computed(
  () => selectedPageCount.value > 0 && !allPagedEntriesSelected.value,
);
const editLineNumbers = computed(() =>
  Array.from({ length: Math.max(1, editContent.value.split('\n').length) }, (_, index) => index + 1),
);
const deleteHasSensitivePath = computed(
  () => deleteTargets.value?.some((entry) => entry.isSensitiveSystemPath) ?? false,
);
const renameHasSensitivePath = computed(() => renameTarget.value?.isSensitiveSystemPath ?? false);
const moveHasSensitivePath = computed(
  () => transferKind.value === 'move' && transferTargets.value.some((entry) => entry.isSensitiveSystemPath),
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
watch([fileSearch, pageSize], () => {
  page.value = 1;
});
watch(pageCount, (value) => {
  if (page.value > value) page.value = value;
});
onMounted(() => {
  if (!safeModeEnabled.value) void loadRoots();
});
watch(safeModeEnabled, (enabled) => {
  if (!enabled) {
    void loadRoots();
    return;
  }
  cancelRequests();
  roots.value = [];
  directory.value = undefined;
  selectedEntries.value = [];
  pageError.value = '';
  rootPickerOpen.value = true;
  createKind.value = undefined;
  entryMenu.value = undefined;
  renameTarget.value = undefined;
  deleteTargets.value = undefined;
  transferKind.value = undefined;
  downloadTargets.value = undefined;
  editTarget.value = undefined;
  editSaveConfirm.value = false;
  editContent.value = '';
  uploadWarning.value = false;
  uploadFiles.value = [];
  uploadDirectoryPath.value = '';
  uploadItems.value = [];
}, { flush: 'sync' });
watch(transferKind, (kind) => {
  if (!kind) {
    requests.get('targetDirectory')?.abort();
    transferTargets.value = [];
    targetDirectory.value = undefined;
    targetPath.value = '';
  }
}, { flush: 'sync' });
watch(editTarget, (entry) => {
  if (!entry) {
    editSaveConfirm.value = false;
    editContent.value = '';
  }
});
onBeforeUnmount(() => {
  isPageActive = false;
  cancelRequests();
  uploadFiles.value = [];
  uploadDirectoryPath.value = '';
  uploadItems.value = [];
  editContent.value = '';
});

async function loadRoots() {
  await runRequest('roots', async (signal) => {
    roots.value = await readJson<Root[]>('/api/files/roots', signal);
  }, '无法读取驱动器');
}
async function browse(path: string) {
  pageError.value = '';
  await runRequest('directory', async (signal) => {
    const result = await readJson<DirectoryResult>(`/api/files/directory?path=${encodeURIComponent(path)}`, signal);
    directory.value = result;
    selectedEntries.value = [];
    rootPickerOpen.value = false;
    fileSearch.value = '';
    page.value = 1;
  }, '无法读取文件夹');
}
async function refreshDirectory(path: string | undefined) {
  if (path && directory.value?.path === path && !loading.value) await browse(path);
}
function toggleRootPicker() {
  rootPickerOpen.value = !rootPickerOpen.value;
}
function normalizePageSize() {
  pageSize.value = Math.max(1, Math.floor(Number(pageSize.value) || 25));
}
function handleRowClick(entry: Entry) {
  if (entry.isDirectory) void browse(entry.path);
}
function handleNameClick(entry: Entry) {
  entryMenu.value = entry;
}
function toggleEntrySelection(entry: Entry, checked: boolean) {
  const index = selectedEntries.value.findIndex((selected) => selected.path === entry.path);
  if (checked && index < 0) selectedEntries.value = [...selectedEntries.value, entry];
  if (!checked && index >= 0)
    selectedEntries.value = selectedEntries.value.filter((selected) => selected.path !== entry.path);
}
function togglePagedSelection(checked: boolean) {
  const pagedPaths = new Set(pagedEntries.value.map((entry) => entry.path));
  selectedEntries.value = checked
    ? [...selectedEntries.value.filter((entry) => !pagedPaths.has(entry.path)), ...pagedEntries.value]
    : selectedEntries.value.filter((entry) => !pagedPaths.has(entry.path));
}
function startCreate() {
  createKind.value = 'folder';
  newName.value = '';
}
function startRename(entry: Entry) {
  renameTarget.value = entry;
  renameName.value = entry.name;
}
function startEntryTransfer(kind: TransferKind, entries?: Entry[]) {
  const targets = entries?.length ? entries : entryMenu.value ? [entryMenu.value] : [];
  if (!targets.length) return;
  transferKind.value = kind;
  transferTargets.value = targets;
  targetDirectory.value = undefined;
  targetPath.value = '';
  entryMenu.value = undefined;
}
function resetTargetDirectory() {
  requests.get('targetDirectory')?.abort();
  requests.delete('targetDirectory');
  targetDirectory.value = undefined;
  targetPath.value = '';
}
async function browseTarget(path: string) {
  targetPath.value = '';
  await runRequest('targetDirectory', async (signal) => {
    const result = await readJson<DirectoryResult>(`/api/files/directory?path=${encodeURIComponent(path)}`, signal);
    targetDirectory.value = result;
    targetPath.value = result.path;
  }, '无法读取目标文件夹');
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
  if (directory.value.isSensitiveSystemPath) uploadWarning.value = true;
  else void uploadSelectedFiles();
}
async function uploadSelectedFiles() {
  const directoryPath = uploadDirectoryPath.value;
  const files = uploadFiles.value;
  if (!directoryPath || !files.length || uploading.value) return;
  uploadWarning.value = false;
  uploadFiles.value = [];
  uploadDirectoryPath.value = '';
  uploadItems.value = files.map((file) => ({ fileName: file.name, status: 'uploading' }));
  const form = new FormData();
  form.append('directory', directoryPath);
  files.forEach((file) => form.append('files', file, file.name));
  await runRequest('upload', async (signal) => {
    try {
      const response = await fetchResponse('/api/files/upload', signal, { method: 'POST', body: form });
      const results = await response.json() as UploadResponse[];
      ensureRequestActive(signal);
      uploadItems.value = results.map((result) => ({
        fileName: result.fileName,
        status: result.success ? 'success' : 'failed',
        error: result.error ?? undefined,
      }));
      await refreshDirectory(directoryPath);
    } catch (exception) {
      ensureRequestActive(signal);
      const message = userErrorMessage(exception, '文件上传失败');
      uploadItems.value = files.map((file) => ({ fileName: file.name, status: 'failed', error: message || undefined }));
      throw exception;
    }
  }, '文件上传失败', false);
}
function requestDownload(entries: Entry[]) {
  downloadTargets.value = entries;
  downloadWarning.value = entries.some((entry) => entry.isSensitiveSystemPath);
}
async function confirmDownload() {
  const entries = downloadTargets.value;
  if (!entries?.length || downloading.value) return;
  downloadWarning.value = false;
  downloadTargets.value = undefined;
  await runRequest('download', async (signal) => {
    const response = await fetchResponse('/api/files/download', signal, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ paths: entries.map((entry) => entry.path) }),
    });
    const blob = await response.blob();
    ensureRequestActive(signal);
    const entry = entries[0];
    const name = entries.length === 1 ? (entry.isDirectory ? `${entry.name}.zip` : entry.name) : 'Download.zip';
    const url = URL.createObjectURL(blob);
    try {
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = name;
      document.body.append(anchor);
      try { anchor.click(); } finally { anchor.remove(); }
    } finally {
      URL.revokeObjectURL(url);
    }
    showToast('下载已开始', 'success');
  }, '文件下载失败', false);
}
async function submitFileOperation(url: string, body: unknown, success: string) {
  const directoryPath = directory.value?.path;
  return await runRequest('write', async (signal) => {
    await fetchResponse(url, signal, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    showToast(success, 'success');
    await refreshDirectory(directoryPath);
    return true;
  }, '文件操作失败', false) ?? false;
}
async function confirmCreate() {
  if (!createKind.value || !directory.value) return;
  const success = await submitFileOperation(
    `/api/files/${createKind.value}`,
    { parentPath: directory.value.path, name: newName.value.trim() },
    createKind.value === 'folder' ? '已新建文件夹' : '已新建文件',
  );
  if (success) createKind.value = undefined;
}
async function confirmRename() {
  if (!renameTarget.value) return;
  const success = await submitFileOperation(
    '/api/files/rename',
    { path: renameTarget.value.path, name: renameName.value.trim() },
    '已重命名',
  );
  if (success) renameTarget.value = undefined;
}
async function confirmDelete() {
  if (!deleteTargets.value) return;
  const success = await submitFileOperation(
    '/api/files/delete',
    { paths: deleteTargets.value.map((entry) => entry.path) },
    `已将 ${deleteTargets.value.length} 项移入回收站`,
  );
  if (success) deleteTargets.value = undefined;
}
async function confirmTransfer() {
  if (!transferKind.value || !targetPath.value) return;
  const success = await submitFileOperation(
    `/api/files/${transferKind.value}`,
    { sourcePaths: transferTargets.value.map((entry) => entry.path), destinationDirectory: targetPath.value },
    transferKind.value === 'copy' ? '已复制' : '已移动',
  );
  if (success) transferKind.value = undefined;
}
async function startEdit(entry: Entry) {
  if (submitting.value) return;
  entryMenu.value = undefined;
  editLineScrollTop.value = 0;
  await runRequest('editRead', async (signal) => {
    const result = await readJson<EditResponse>(`/api/files/edit?path=${encodeURIComponent(entry.path)}`, signal);
    editTarget.value = entry;
    editContent.value = result.content;
    editOriginalLength.value = result.length;
    editOriginalLastWriteTime.value = result.lastWriteTime;
    editEncoding.value = result.encoding;
  }, '无法读取文件');
}
function requestEditSave() {
  if (editTarget.value && !submitting.value) editSaveConfirm.value = true;
}
async function confirmEditSave() {
  const entry = editTarget.value;
  if (!entry || submitting.value) return;
  const body = {
    path: entry.path,
    content: editContent.value,
    originalLength: editOriginalLength.value,
    originalLastWriteTime: editOriginalLastWriteTime.value,
    encoding: editEncoding.value,
  };
  editSaveConfirm.value = false;
  await runRequest('write', async (signal) => {
    await fetchResponse('/api/files/edit', signal, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const result = await readJson<EditResponse>(`/api/files/edit?path=${encodeURIComponent(entry.path)}`, signal);
    if (editTarget.value === entry) {
      editOriginalLength.value = result.length;
      editOriginalLastWriteTime.value = result.lastWriteTime;
    }
    showToast('文件已保存', 'success');
  }, '文件保存失败', false);
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
  return new Intl.DateTimeFormat('zh-CN', { dateStyle: 'short', timeStyle: 'short' }).format(new Date(value));
}
</script>

<template>
  <section
    class="files-view"
    :class="{ 'safe-mode-locked': safeModeEnabled }"
    :inert="safeModeEnabled"
  >
    <p v-if="pageError" class="status-message error" role="alert">{{ pageError }}</p>
    <section class="files-panel">
      <input ref="uploadInput" class="file-upload-input" type="file" multiple @change="selectUploadFiles" />
      <header class="files-header">
        <div class="files-location">
          <button class="files-crumb" type="button" :aria-expanded="rootPickerOpen" @click="toggleRootPicker">
            选择驱动器
          </button>
          <nav v-if="directory" class="files-breadcrumb" aria-label="当前路径">
            <template v-for="(crumb, index) in breadcrumbs" :key="crumb.path">
              <span v-if="index" class="files-breadcrumb-separator" aria-hidden="true">&gt;</span>
              <button
                v-if="index < breadcrumbs.length - 1"
                class="files-breadcrumb-link"
                type="button"
                @click="browse(crumb.path)"
              >
                {{ crumb.name }}
              </button>
              <span v-else class="files-breadcrumb-current" aria-current="page">{{ crumb.name }}</span>
            </template>
          </nav>
          <p v-else>选择驱动器开始浏览</p>
        </div>
        <div class="files-header-actions">
          <button
            v-if="directory?.parentPath"
            class="button button-secondary"
            type="button"
            @click="browse(directory!.parentPath!)"
          >
            后退
          </button>
          <button
            v-if="directory"
            class="button button-secondary files-refresh"
            type="button"
            :disabled="loading"
            @click="browse(directory!.path)"
          >
            {{ loading ? '刷新中…' : '刷新' }}
          </button>
        </div>
      </header>
      <div v-if="rootPickerOpen" class="file-root-list">
        <button
          v-for="root in roots"
          :key="root.path"
          class="file-root"
          type="button"
          @click="browse(root.path)"
        >
          <strong>{{ root.name }}</strong>
          <small>{{ root.path }} · {{ root.kind }}</small>
        </button>
      </div>
      <template v-if="directory">
        <div class="files-toolbar">
          <label class="field-control files-search">
            <input v-model="fileSearch" type="search" placeholder="按文件名搜索" aria-label="当前目录搜索" />
          </label>
          <div class="files-toolbar-actions">
            <template v-if="selectedEntries.length">
              <button class="button button-secondary" type="button" :disabled="downloading" @click="requestDownload([...selectedEntries])">
                下载
              </button>
              <button class="button button-secondary" type="button" @click="startEntryTransfer('copy', [...selectedEntries])">
                复制
              </button>
              <button class="button button-secondary" type="button" @click="startEntryTransfer('move', [...selectedEntries])">
                移动
              </button>
              <button class="button button-danger" type="button" @click="deleteTargets = [...selectedEntries]">
                删除
              </button>
            </template>
            <button class="button button-secondary" type="button" :disabled="uploading" @click="chooseUpload">上传</button>
            <button class="button button-primary" type="button" @click="startCreate">新建</button>
          </div>
        </div>
        <div class="file-list" :class="{ 'is-loading': loading }">
          <div class="file-list-head">
            <span class="file-select-cell">
              <input
                type="checkbox"
                :checked="allPagedEntriesSelected"
                :indeterminate="somePagedEntriesSelected"
                aria-label="选择当前页文件"
                @click.stop
                @change="togglePagedSelection(($event.target as HTMLInputElement).checked)"
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
              <input
                type="checkbox"
                :checked="selectedEntries.some((selected) => selected.path === entry.path)"
                :aria-label="`选择 ${entry.name}`"
                @change="toggleEntrySelection(entry, ($event.target as HTMLInputElement).checked)"
              />
            </label>
            <button class="file-name" type="button" @click.stop="handleNameClick(entry)">
              <strong>{{ entry.name }}</strong>
              <small v-if="entry.isReparsePoint">重解析点</small>
            </button>
            <span class="file-type">{{ entry.isDirectory ? '目录' : '文件' }}</span>
            <span class="file-size">{{ formatSize(entry.size) }}</span>
            <time class="file-time">{{ formatTime(entry.lastWriteTime) }}</time>
          </article>
          <p v-if="!pagedEntries.length && !loading" class="program-empty">
            {{ filteredEntries.length ? '当前页没有文件' : '当前目录没有符合条件的文件' }}
          </p>
        </div>
        <div v-if="filteredEntries.length" class="program-pagination">
          <label class="field-control page-size-control">
            <span>每页</span>
            <input
              v-model.number="pageSize"
              type="number"
              min="1"
              step="1"
              inputmode="numeric"
              aria-label="每页项目数"
              @change="normalizePageSize"
            />
          </label>
          <div class="pagination-actions">
            <button class="button button-secondary" type="button" :disabled="page === 1" @click="page--">
              上一页
            </button>
            <span>{{ page }} / {{ pageCount }}</span>
            <button
              class="button button-secondary"
              type="button"
              :disabled="page === pageCount"
              @click="page++"
            >
              下一页
            </button>
          </div>
        </div>
      </template>
    </section>
    <AppModal
      v-if="uploadWarning"
      title="确认上传"
      @close="
        uploadWarning = false;
        uploadFiles = [];
      "
    >
      <p class="file-sensitive-warning">
        当前目录位于 Windows 系统目录或重要程序目录中，上传文件可能影响系统或相关程序。确定要继续吗？
      </p>
      <template #actions>
        <button
          class="button button-secondary"
          type="button"
          @click="
            uploadWarning = false;
            uploadFiles = [];
          "
        >
          取消
        </button>
        <button class="button button-primary" type="button" @click="uploadSelectedFiles">继续上传</button>
      </template>
    </AppModal>
    <AppModal v-if="uploadItems.length" title="上传状态" :dismissible="!uploading" @close="uploadItems = []">
      <div class="file-upload-status">
        <p v-for="item in uploadItems" :key="item.fileName">
          <strong>{{ item.fileName }}</strong>
          <span v-if="item.status === 'uploading'">上传中…</span>
          <span v-else-if="item.status === 'success'" class="status-success">上传成功</span>
          <span v-else class="status-error">上传失败{{ item.error ? `：${item.error}` : '' }}</span>
        </p>
      </div>
      <template #actions>
        <button class="button button-secondary" type="button" :disabled="uploading" @click="uploadItems = []">关闭</button>
      </template>
    </AppModal>
    <AppModal v-if="createKind" :dismissible="!submitting" title="新建" @close="createKind = undefined">
      <label class="field-control">
        <span>类型</span>
        <AppSelect
          v-model="createKind"
          :disabled="submitting"
          :options="[
            { value: 'folder', label: '文件夹' },
            { value: 'file', label: '文件' },
          ]"
        />
      </label>
      <label class="field-control">
        <span>名称</span>
        <input
          v-model="newName"
          :disabled="submitting"
          autofocus
          :placeholder="createKind === 'folder' ? '例如 新建文件夹' : '例如 notes.txt'"
          @keyup.enter="confirmCreate"
        />
      </label>
      <template #actions>
        <button class="button button-secondary" type="button" :disabled="submitting" @click="createKind = undefined">取消</button>
        <button class="button button-primary" type="button" :disabled="submitting" @click="confirmCreate">
          创建
        </button>
      </template>
    </AppModal>
    <AppModal v-if="entryMenu" class="file-entry-menu" :title="entryMenu.name" @close="entryMenu = undefined">
      <p>{{ entryMenu.isDirectory ? '选择要对这个文件夹执行的操作' : '选择要对这个文件执行的操作' }}</p>
      <template #actions>
        <button
          v-if="entryMenu.isDirectory"
          class="button button-primary"
          type="button"
          @click="
            browse(entryMenu!.path);
            entryMenu = undefined;
          "
        >
          进入
        </button>
        <button
          v-else
          class="button button-primary"
          type="button"
          :disabled="editLoading"
          @click="startEdit(entryMenu!)"
        >
          编辑
        </button>
        <button
          class="button button-secondary"
          type="button"
          @click="
            requestDownload([entryMenu!]);
            entryMenu = undefined;
          "
        >
          下载
        </button>
        <button
          class="button button-secondary"
          type="button"
          @click="
            startRename(entryMenu!);
            entryMenu = undefined;
          "
        >
          重命名
        </button>
        <button class="button button-secondary" type="button" @click="startEntryTransfer('copy')">
          复制
        </button>
        <button class="button button-secondary" type="button" @click="startEntryTransfer('move')">
          移动
        </button>
        <button
          class="button button-danger"
          type="button"
          @click="
            deleteTargets = [entryMenu!];
            entryMenu = undefined;
          "
        >
          删除
        </button>
      </template>
    </AppModal>
    <AppModal v-if="renameTarget" :dismissible="!submitting" title="重命名" @close="renameTarget = undefined">
      <p v-if="renameHasSensitivePath" class="file-sensitive-warning">
        此项目位于 Windows 系统目录或重要程序目录中，重命名可能导致系统或相关程序无法正常运行。确定要继续吗？
      </p>
      <label class="field-control">
        <span>新名称</span>
        <input v-model="renameName" :disabled="submitting" autofocus @keyup.enter="confirmRename" />
      </label>
      <template #actions>
        <button class="button button-secondary" type="button" :disabled="submitting" @click="renameTarget = undefined">取消</button>
        <button class="button button-primary" type="button" :disabled="submitting" @click="confirmRename">
          保存
        </button>
      </template>
    </AppModal>
    <AppModal v-if="deleteTargets" :dismissible="!submitting" title="确认移入回收站" @close="deleteTargets = undefined">
      <p>将把 {{ deleteTargets.length }} 项移入 Windows 回收站，确认继续吗？</p>
      <p v-if="deleteHasSensitivePath" class="file-sensitive-warning">
        其中包含 Windows
        系统目录或重要程序目录中的项目，删除可能导致系统或相关程序无法正常运行。确定要继续吗？
      </p>
      <template #actions>
        <button class="button button-secondary" type="button" :disabled="submitting" @click="deleteTargets = undefined">取消</button>
        <button class="button button-danger" type="button" :disabled="submitting" @click="confirmDelete">
          移入回收站
        </button>
      </template>
    </AppModal>
    <AppModal
      v-if="transferKind"
      :dismissible="!submitting"
      :title="transferKind === 'copy' ? '复制到文件夹' : '移动到文件夹'"
      @close="transferKind = undefined"
    >
      <div class="file-target-picker" :inert="submitting">
        <p>已选择 {{ transferTargets.length }} 项。请选择目标文件夹：</p>
        <p v-if="moveHasSensitivePath" class="file-sensitive-warning">
          其中包含 Windows
          系统目录或重要程序目录中的项目，移动可能导致系统或相关程序无法正常运行。确定要继续吗？
        </p>
        <div v-if="!targetDirectory" class="file-root-list">
          <button
            v-for="root in roots"
            :key="root.path"
            class="file-root"
            type="button"
            @click="browseTarget(root.path)"
          >
            <strong>{{ root.name }}</strong>
            <small>{{ root.path }}</small>
          </button>
        </div>
        <template v-else>
          <p class="file-target-path">{{ targetDirectory.path }}</p>
          <div class="file-target-actions">
            <button
              v-if="targetDirectory.parentPath"
              class="button button-secondary"
              type="button"
              @click="browseTarget(targetDirectory!.parentPath!)"
            >
              返回上级
            </button>
            <button
              class="button button-secondary"
              type="button"
              @click="
                resetTargetDirectory();
              "
            >
              选择其他根目录
            </button>
          </div>
          <div class="file-target-list">
            <button
              v-for="entry in targetDirectory.entries.filter((item) => item.isDirectory)"
              :key="entry.path"
              type="button"
              @click="browseTarget(entry.path)"
            >
              {{ entry.name }}
              <small v-if="entry.isReparsePoint">重解析点</small>
            </button>
            <p v-if="!targetDirectory.entries.some((item) => item.isDirectory)" class="program-empty">
              没有可进入的子文件夹
            </p>
          </div>
        </template>
      </div>
      <template #actions>
        <button class="button button-secondary" type="button" :disabled="submitting" @click="transferKind = undefined">取消</button>
        <button
          class="button button-primary"
          type="button"
          :disabled="submitting || !targetPath || requests.has('targetDirectory')"
          @click="confirmTransfer"
        >
          {{ transferKind === 'copy' ? '复制到此处' : '移动到此处' }}
        </button>
      </template>
    </AppModal>
  </section>
  <AppModal
    v-if="downloadTargets"
    title="确认下载"
    @close="
      downloadTargets = undefined;
      downloadWarning = false;
    "
  >
    <p v-if="downloadWarning" class="file-sensitive-warning">
      其中包含 Windows 系统目录或重要程序目录中的项目，下载操作可能触发系统权限限制。确定要继续吗？
    </p>
    <p v-else>将下载所选项目，是否继续？</p>
    <template #actions>
      <button
        class="button button-secondary"
        type="button"
        @click="
          downloadTargets = undefined;
          downloadWarning = false;
        "
      >
        取消
      </button>
      <button class="button button-primary" type="button" @click="confirmDownload">继续下载</button>
    </template>
  </AppModal>
  <AppModal
    v-if="editTarget"
    :dismissible="!submitting"
    class="file-edit-modal"
    :title="`编辑：${editTarget.name}`"
    :dismiss-on-backdrop="false"
    @close="editTarget = undefined"
  >
    <div class="file-editor">
      <div class="file-editor-lines" aria-hidden="true">
        <div class="file-editor-line-content" :style="{ transform: `translateY(-${editLineScrollTop}px)` }">
          <span v-for="line in editLineNumbers" :key="line">{{ line }}</span>
        </div>
      </div>
      <textarea
        v-model="editContent"
        :readonly="submitting"
        aria-label="文件内容"
        wrap="off"
        spellcheck="false"
        @scroll="syncEditLines"
      ></textarea>
    </div>
    <template #actions>
      <button class="button button-secondary" type="button" :disabled="submitting" @click="editTarget = undefined">关闭</button>
      <button class="button button-primary" type="button" :disabled="submitting" @click="requestEditSave">保存</button>
    </template>
  </AppModal>
  <AppModal v-if="editSaveConfirm" title="确认保存" @close="editSaveConfirm = false">
    <p>确定保存对「{{ editTarget?.name }}」的修改吗？</p>
    <template #actions>
      <button class="button button-secondary" type="button" @click="editSaveConfirm = false">取消</button>
      <button class="button button-primary" type="button" :disabled="submitting" @click="confirmEditSave">保存</button>
    </template>
  </AppModal>
</template>

<style>
.files-view { display: grid; width: 100%; max-width: none; gap: 18px; }
.files-panel { padding: 18px 20px 20px; border: 1px solid var(--border); border-radius: var(--radius-lg); background: var(--surface); box-shadow: 0 10px 28px rgb(23 43 77 / 4%); }
.files-header { display: flex; align-items: center; justify-content: space-between; min-height: 54px; gap: 16px; padding-bottom: 14px; border-bottom: 1px solid var(--border); }
.files-header p { margin: 6px 0 0; color: var(--text-muted); font-size: 13px; }
.files-location { display: flex; align-items: center; min-width: 0; gap: 10px; }
.files-header-actions, .files-toolbar-actions, .file-target-actions { display: flex; flex-wrap: wrap; gap: 8px; }
.files-header-actions { align-items: center; }
.files-crumb, .files-breadcrumb-link { font: inherit; }
.files-crumb { display: inline-flex; align-items: center; width: fit-content; gap: 7px; padding: 6px 10px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface-subtle); color: var(--text); font-size: 13px; font-weight: 650; }
.files-crumb::after { content: '▾'; color: var(--text-muted); font-size: 11px; transition: transform .15s ease; }
.files-crumb[aria-expanded='true']::after { transform: rotate(180deg); }
.files-crumb:hover, .files-crumb:focus-visible { border-color: var(--accent-focus-ring); background: var(--accent-bg); color: var(--accent-text); outline: 0; }
.files-breadcrumb { display: flex; min-width: 0; max-width: min(760px, 70vw); gap: 7px; padding-bottom: 2px; overflow-x: auto; color: var(--text-muted); font-family: var(--font-mono); font-size: 12px; scrollbar-width: thin; white-space: nowrap; }
.files-breadcrumb-link { flex: 0 0 auto; padding: 0; border: 0; background: transparent; color: var(--text); font-weight: 500; }
.files-breadcrumb-link:hover, .files-breadcrumb-link:focus-visible { color: var(--text); background: var(--surface-hover); text-decoration: none; outline: 0; }
.files-breadcrumb-separator, .files-breadcrumb-current { flex: 0 0 auto; }
.files-breadcrumb-current { color: var(--text); font-weight: 650; }
.file-root-list { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 12px; margin-top: 14px; }
.file-root { display: grid; gap: 5px; min-height: 88px; padding: 14px; border: 1px solid var(--border); border-radius: var(--radius-md); background: var(--surface-subtle); color: var(--text); text-align: left; }
.file-root:hover { border-color: var(--accent-border); background: var(--accent-bg); }
.file-root small { overflow: hidden; color: var(--text-muted); text-overflow: ellipsis; white-space: nowrap; }
.files-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-top: 16px; padding: 2px 0; }
.files-search { width: min(100%, 360px); }
.files-search input { width: 100%; }
.files-toolbar-actions { margin-left: auto; }
.file-upload-input { display: none; }
.file-upload-status { display: grid; gap: 8px; }
.file-upload-status p { display: flex; justify-content: space-between; gap: 16px; margin: 0; }
.file-list { margin-top: 14px; overflow: hidden; border: 1px solid var(--border); border-radius: var(--radius-md); }
.file-list.is-loading { opacity: .6; }
.file-list-head, .file-row { display: grid; grid-template-columns: 30px minmax(180px, 1fr) minmax(80px, .35fr) 88px 132px; align-items: center; gap: 7px; padding: 11px 12px; }
.file-list-head { min-height: 50px; border-bottom: 1px solid var(--border); background: var(--surface-subtle); color: var(--text-muted); font-size: 12px; font-weight: 650; }
.file-row { min-height: 62px; border-bottom: 1px solid var(--border); }
.file-row:last-of-type { border-bottom: 0; }
.file-row.is-browsable { cursor: pointer; }
.file-row.is-browsable:hover { background: var(--surface-hover); }
.file-select-cell { display: grid; min-width: 0; place-items: center; }
.file-select-cell input { width: 16px; height: 16px; margin: 0; accent-color: var(--accent-solid); }
.file-name { display: grid; min-width: 0; max-width: 100%; justify-self: start; gap: 3px; padding: 6px 8px 6px 0; border: 1px solid transparent; border-radius: var(--radius-sm); background: transparent; color: var(--text); text-align: left; }
.file-name:hover { color: var(--accent-text); text-decoration: none; }
.file-name:focus-visible { outline: 2px solid var(--accent-focus-ring); outline-offset: 2px; }
.file-name strong, .file-type, .file-size, .file-time { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.file-name small { color: var(--warning-text); font-size: 11px; }
.file-type, .file-size, .file-time { color: var(--text-muted); font-size: 13px; }
.file-target-picker { display: grid; gap: 12px; }
.file-target-picker > p { margin: 0; }
.file-target-path { overflow: hidden; padding: 9px 10px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface-subtle); color: var(--text-muted); font-family: var(--font-mono); font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
.file-target-list { display: grid; max-height: 260px; overflow: auto; border: 1px solid var(--border); border-radius: var(--radius-sm); }
.file-target-list button { display: flex; align-items: center; justify-content: space-between; min-height: 42px; padding: 0 12px; border: 0; border-bottom: 1px solid var(--border); background: transparent; color: var(--text); text-align: left; }
.file-target-list button:hover { background: var(--surface-hover); color: var(--accent-text); }
.file-target-list button small { color: var(--warning-text); }
.file-target-list .program-empty { padding: 0 12px; }
.file-list > .program-empty { margin: 0; padding: 14px 12px; }
.file-sensitive-warning { margin: 0 0 12px; padding: 10px 12px; border-left: 3px solid var(--danger-border); border-radius: var(--radius-sm); background: var(--danger-bg); color: var(--text); font-size: 13px; line-height: 1.55; }
.file-edit-modal .modal-content { display: flex; min-height: 0; flex: 1 1 auto; overflow: hidden; }
.file-edit-modal .app-modal { width: min(96vw, 1200px); }
.file-editor { display: grid; grid-template-columns: 44px minmax(0, 1fr); grid-template-rows: minmax(0, 1fr); width: 100%; height: min(70vh, 760px); min-height: 0; min-width: 0; overflow: hidden; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface-subtle); font-family: var(--font-mono); }
.file-editor-lines { min-height: 0; overflow: hidden; padding: 10px 8px; color: var(--text-muted); font-size: 12px; line-height: 24px; user-select: none; }
.file-editor-line-content { display: flex; flex-direction: column; align-items: end; }
.file-editor textarea { display: block; width: 100%; height: auto; min-width: 0; min-height: 0; padding: 10px 12px; border: 0; border-left: 1px solid var(--border); border-radius: 0; outline: 0; background: var(--surface); color: var(--text); font: inherit; line-height: 1.5; resize: none; tab-size: 4; white-space: pre; overflow: auto; overflow-wrap: normal; scrollbar-gutter: stable both-edges; }
@media (max-width: 767px) {
  .files-panel { padding: 16px; }
  .files-header { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: start; padding-bottom: 12px; }
  .files-location { display: contents; }
  .files-crumb { grid-column: 1; grid-row: 1; min-height: 36px; }
  .files-breadcrumb, .files-location > p { grid-column: 1 / -1; grid-row: 2; }
  .files-location > p { margin: 8px 0 0; }
  .files-breadcrumb { width: 100%; max-width: 100%; padding: 8px 0 3px; border-top: 1px solid var(--border); }
  .files-header-actions { grid-column: 2; grid-row: 1; align-items: stretch; }
  .files-header-actions .button { flex: 1; width: 72px; min-width: 72px; }
  .files-toolbar { align-items: stretch; flex-direction: column; gap: 10px; margin-top: 14px; }
  .files-search { width: 100%; }
  .files-toolbar-actions { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); width: 100%; min-width: 0; margin-left: 0; }
  .files-toolbar-actions .button { flex: 1; width: 100%; min-width: 0; padding-inline: 8px; }
  .file-root-list { grid-template-columns: 1fr; gap: 8px; margin-top: 12px; }
  .file-root { min-height: 72px; padding: 12px; }
  .file-list-head, .file-row { grid-template-columns: 30px minmax(0, 1fr) 96px; }
  .file-list-head { grid-template-rows: repeat(2, auto); gap: 3px 6px; padding: 8px 0; border-inline: 0; border-radius: 0; }
  .file-list-head span { font-size: 12px; line-height: 1.3; font-weight: 650; text-align: left; }
  .file-list-head span:nth-of-type(1) { grid-column: 1; grid-row: 1 / span 2; padding-inline: 0; }
  .file-list-head span:nth-of-type(2) { grid-column: 2; grid-row: 1; padding-inline: 4px; }
  .file-list-head span:nth-of-type(3) { grid-column: 3; grid-row: 1; padding-inline: 0; color: var(--text-muted); }
  .file-list-head span:nth-of-type(4) { grid-column: 2; grid-row: 2; padding-inline: 4px; color: var(--text-muted); }
  .file-list-head span:nth-of-type(5) { grid-column: 3; grid-row: 2; padding-inline: 0; }
  .file-row { gap: 6px; padding: 12px 0; -webkit-tap-highlight-color: transparent; }
  .file-select-cell { grid-column: 1; grid-row: 1 / span 2; }
  .file-name { grid-column: 2; grid-row: 1; padding-left: 4px; }
  .file-name:focus-visible { outline: 0; color: var(--accent-text); text-decoration: underline; }
  .file-type { grid-column: 3; grid-row: 1; }
  .file-size, .file-time { grid-row: 2; font-size: 11px; }
  .file-size { grid-column: 2; padding-inline: 8px; }
  .file-time { grid-column: 3; margin-left: 0; font-size: 10px; }
  .file-type { font-size: 11px; }
  .file-target-list { max-height: 220px; }
  .file-editor { height: 58vh; grid-template-columns: 34px minmax(0, 1fr); }
  .file-editor-lines { padding-inline: 5px; font-size: 11px; line-height: 18px; }
  .file-editor textarea { min-height: 48vh; padding-inline: 8px; font-size: 12px; }
  .file-edit-modal .app-modal { width: min(100%, 560px); }
  .file-entry-menu .modal-actions { display: grid; grid-template-columns: 1fr; gap: 8px; padding: 12px 16px 16px; }
  .file-entry-menu .modal-actions .button { width: 100%; min-height: 42px; min-width: 0; padding: 0 12px; }
  .file-entry-menu .modal-actions .button-danger { margin-top: 4px; }
  .files-view .program-pagination { display: flex; flex-direction: row; align-items: center; justify-content: space-between; gap: 8px; }
  .files-view .page-size-control { width: auto; grid-template-columns: auto 64px; flex: 0 0 auto; }
  .files-view .pagination-actions { display: flex; width: auto; gap: 6px; }
  .files-view .pagination-actions .button { width: auto; min-width: 68px; padding-inline: 8px; }
}
</style>
