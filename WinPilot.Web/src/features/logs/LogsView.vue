<script setup lang="ts">
import { NButton, NCard, NCollapse, NCollapseItem, NEmpty, NFormItem, NInput, NScrollbar, NTag } from 'naive-ui';
import { useId, computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { readApiError, apiFetch, isAbortError, userErrorMessage } from '../../api/client';
import AppLoadError from '../../components/AppLoadError.vue';
import AppLoading from '../../components/AppLoading.vue';
import AppSelect from '../../components/AppSelect.vue';
import AppMultiSelect from '../../components/AppMultiSelect.vue';
import { useDelayedLoading } from '../../ui/useDelayedLoading';

const formId = useId();

interface LogEntry {
  id: number;
  time: string;
  level: string;
  source: string;
  message: string;
  details: string | null;
}
interface LogPage {
  entries: LogEntry[];
  hasMore: boolean;
  snapshot: number;
}

const dates = ref<string[]>([]);
const selectedDate = ref('');
const levelOptions = ['INF', 'WRN', 'ERR', 'DBG'] as const;
const levelSelectOptions = levelOptions.map((level) => ({ value: level, label: level }));
const dateOptions = computed(() => dates.value.map((date) => ({ value: date, label: date })));
const selectedLevels = ref<string[]>(levelOptions.filter((level) => level !== 'DBG'));
const levelLabel = computed(() =>
  selectedLevels.value.length ? `已选择 ${selectedLevels.value.length} 项` : '未选择',
);
const keyword = ref('');
const entries = ref<LogEntry[]>([]);
const hasMore = ref(false);
const isLoading = ref(false);
const isRefreshing = ref(false);
const isLoadingMore = ref(false);
const logsLoaded = ref(false);
const pageError = ref('');
const retryLoadingMore = ref(false);
const hasLogs = computed(() => dates.value.length > 0);
let isPageActive = false;
let loadController: AbortController | undefined;
let loadedQuery = '';
let snapshot: number | undefined;

onMounted(() => {
  isPageActive = true;
  void refreshLogs(false);
});
onBeforeUnmount(() => {
  isPageActive = false;
  loadController?.abort();
  loadController = undefined;
  isLoading.value = false;
  isRefreshing.value = false;
  isLoadingMore.value = false;
  clearEntries();
  dates.value = [];
  pageError.value = '';
});

function clearEntries() {
  entries.value = [];
  hasMore.value = false;
  snapshot = undefined;
  loadedQuery = '';
}
function ensureActive(signal: AbortSignal) {
  if (!isPageActive || signal.aborted) throw new DOMException('请求已取消', 'AbortError');
}
async function readJson<T>(url: string, signal: AbortSignal): Promise<T> {
  const response = await apiFetch(url, { signal });
  if (!response.ok) {
    throw new Error(await readApiError(response, '无法读取日志，请稍后重试'));
  }
  const result = (await response.json()) as T;
  ensureActive(signal);
  return result;
}
async function load(
  action: (signal: AbortSignal) => Promise<void>,
  showRefreshFeedback = false,
  loadingMore = false,
) {
  if (!isPageActive) return;
  loadController?.abort();
  const controller = new AbortController();
  loadController = controller;
  isLoading.value = true;
  isRefreshing.value = showRefreshFeedback;
  isLoadingMore.value = loadingMore;
  try {
    await action(controller.signal);
    ensureActive(controller.signal);
    logsLoaded.value = true;
    pageError.value = '';
    retryLoadingMore.value = false;
  } catch (exception) {
    if (
      isPageActive &&
      loadController === controller &&
      !controller.signal.aborted &&
      !isAbortError(exception)
    ) {
      pageError.value = userErrorMessage(exception, '无法读取日志，请稍后重试');
      retryLoadingMore.value = loadingMore;
    }
  } finally {
    if (loadController === controller) {
      loadController = undefined;
      isLoading.value = false;
      isRefreshing.value = false;
      isLoadingMore.value = false;
    }
  }
}
function toggleLevel(level: string, checked: boolean) {
  if (!levelOptions.some((value) => value === level)) return;
  selectedLevels.value = checked
    ? [...new Set([...selectedLevels.value, level])]
    : selectedLevels.value.filter((value) => value !== level);
  void loadEntries(true);
}
async function refreshLogs(showRefreshFeedback = true) {
  await load(
    async (signal) => {
      const result = await readJson<Array<{ date: string }>>('/api/logs/dates', signal);
      ensureActive(signal);
      dates.value = result.map((item) => item.date);
      if (!dates.value.includes(selectedDate.value)) selectedDate.value = dates.value[0] ?? '';
      await readEntries(signal, true);
    },
    showRefreshFeedback,
  );
}
async function loadEntries(reset: boolean) {
  if (!reset && (isLoading.value || !hasMore.value)) return;
  await load((signal) => readEntries(signal, reset), false, !reset);
}
function retryLoad() {
  return retryLoadingMore.value ? loadEntries(false) : refreshLogs();
}
async function readEntries(signal: AbortSignal, reset: boolean) {
  if (!selectedDate.value || !selectedLevels.value.length) {
    clearEntries();
    return;
  }
  const query = new URLSearchParams({ date: selectedDate.value, limit: '100' });
  query.set(
    'level',
    selectedLevels.value.flatMap((level) => (level === 'ERR' ? ['ERR', 'FTL'] : [level])).join(','),
  );
  if (keyword.value.trim()) query.set('keyword', keyword.value.trim());
  const queryKey = query.toString();
  // 同一查询的后续分页沿用首次快照，避免新增日志把偏移位置推移，造成重复或遗漏。
  reset ||= loadedQuery !== queryKey || snapshot === undefined;
  const offset = reset ? 0 : entries.value.length;
  query.set('offset', String(offset));
  if (!reset && snapshot !== undefined) query.set('snapshot', String(snapshot));
  const page = await readJson<LogPage>(`/api/logs?${query}`, signal);
  ensureActive(signal);
  entries.value = reset ? page.entries : [...entries.value, ...page.entries];
  hasMore.value = page.hasMore;
  if (reset) snapshot = page.snapshot;
  loadedQuery = queryKey;
}
const showRefreshLoading = useDelayedLoading(() => isRefreshing.value);
const showFilterLoading = useDelayedLoading(
  () => isLoading.value && logsLoaded.value && !isRefreshing.value && !isLoadingMore.value,
);
const showMoreLoading = useDelayedLoading(() => isLoading.value && isLoadingMore.value);
</script>

<template>
  <section class="logs-view load-error-layout">
    <AppLoadError :show="Boolean(pageError)" :retry="retryLoad">
      {{ pageError }}
    </AppLoadError>
    <NCard tag="div" content-class="logs-filters" aria-label="日志筛选">
      <NFormItem
        label="日志日期"
        :label-props="{ id: (`${formId}-field-0`) + '-label' }"
        :show-feedback="false"
        class="form-field logs-select-control logs-date-control"
      >
        <AppSelect
          :labelled-by="(`${formId}-field-0`) + '-label'"
          :id="`${formId}-field-0`"
          v-model="selectedDate"
          :options="dateOptions"
          :disabled="isLoading || !hasLogs"
          @change="loadEntries(true)"
        />
      </NFormItem>
      <NFormItem
        label="日志级别"
        :label-props="{ id: (`${formId}-field-1`) + '-label' }"
        :show-feedback="false"
        class="form-field logs-select-control"
      >
        <AppMultiSelect
          :labelled-by="(`${formId}-field-1`) + '-label'"
          :id="`${formId}-field-1`"
          :model-value="selectedLevels"
          :options="levelSelectOptions"
          :label="levelLabel"
          :disabled="isLoading || !hasLogs"
          @toggle="toggleLevel"
        />
      </NFormItem>
      <NFormItem
        label="关键词"
        :label-props="{ for: `${formId}-field-2` }"
        :show-feedback="false"
        class="form-field logs-search"
      >
        <NInput
          v-model:value="keyword"
          clearable
          placeholder="搜索正文或来源"
          :disabled="isLoading || !hasLogs"
          @keyup.enter="loadEntries(true)"
          :input-props="{ id: `${formId}-field-2`, role: 'searchbox', enterkeyhint: 'search' }"
        />
      </NFormItem>
      <NButton
        class="logs-filter-button"
        :loading="showFilterLoading"
        :aria-busy="isLoading && !isRefreshing && !isLoadingMore"
        attr-type="button"
        :disabled="isLoading || !hasLogs"
        @click="loadEntries(true)"
      >
        筛选
      </NButton>
      <NButton
        class="logs-refresh-button"
        :loading="showRefreshLoading"
        :aria-busy="isRefreshing"
        aria-label="刷新"
        attr-type="button"
        :disabled="isLoading && !showRefreshLoading"
        @click="refreshLogs()"
        type="primary"
      >
        刷新
      </NButton>
    </NCard>
    <AppLoading v-if="isLoading && !logsLoaded && !pageError" label="正在读取日志" />
    <NCard
      v-if="!hasLogs && logsLoaded && !pageError"
      class="logs-empty-card"
      content-class="logs-empty"
      :theme-overrides="{ color: 'transparent', borderColor: 'var(--border-strong)' }"
    ><NEmpty :show-icon="false" description="暂无日志" /></NCard>
    <NCard
      tag="div"
      v-else-if="entries.length"
      content-class="log-list"
      :content-style="{ padding: 0 }"
      class="app-list-card"
    >
      <article
        v-for="entry in entries"
        :key="`${selectedDate}-${entry.id}`"
        class="log-entry"
        :class="{ 'log-entry-error': entry.level === 'ERR' || entry.level === 'FTL' }"
      >
        <div class="log-entry-meta">
          <time>{{ entry.time }}</time>
          <NTag
            class="log-level"
            size="small"
            :bordered="false"
            :type="entry.level === 'ERR' || entry.level === 'FTL' ? 'error' : entry.level === 'WRN' ? 'warning' : 'default'"
          >{{
            entry.level
          }}</NTag>
          <span class="log-source">{{ entry.source }}</span>
        </div>
        <p class="log-message">{{ entry.message }}</p>
        <NCollapse v-if="entry.details" class="log-details">
          <NCollapseItem name="details">
            <template #header="{ collapsed }">
              <NButton text attr-type="button" :aria-expanded="!collapsed">查看异常详情</NButton>
            </template>
            <NScrollbar class="log-details-scrollbar" x-scrollable><pre>{{ entry.details }}</pre></NScrollbar>
          </NCollapseItem>
        </NCollapse>
      </article>
      <NButton
        v-if="hasMore"
        class="log-more-button"
        :loading="showMoreLoading"
        :aria-busy="isLoadingMore"
        attr-type="button"
        :disabled="isLoading || !hasLogs"
        @click="loadEntries(false)"
      >
        加载更早的日志
      </NButton>
    </NCard>
    <NCard
      v-else-if="logsLoaded || pageError"
      class="logs-empty-card"
      content-class="logs-empty"
      :theme-overrides="{ color: 'transparent', borderColor: 'var(--border-strong)' }"
    ><NEmpty :show-icon="false" :description="pageError ? '无法加载日志' : '没有符合条件的日志'" /></NCard>
  </section>
</template>

<style>
.app-list-card { overflow: hidden; }
</style>

<style scoped>
.logs-view {
  display: grid;
  gap: var(--page-card-row-gap) 18px;
}
.logs-view :deep(.logs-filters) {
  display: flex;
  flex-wrap: wrap;
  align-items: end;
  gap: 14px;
}
.logs-search {
  flex: 1 1 220px;
}
.logs-search input {
  width: 100%;
}
.logs-filter-button,
.logs-refresh-button {
  flex: 0 0 auto;
}
.logs-view :deep(.log-list) {
  overflow: hidden;
}
.log-entry {
  padding: 16px 18px;
  border-bottom: 1px solid var(--border);
}
.log-entry:last-child {
  border-bottom: 0;
}
.log-entry, .log-entry-meta, .log-source, .log-message, .log-details pre {
  transition:
    background-color .3s cubic-bezier(.4, 0, .2, 1),
    color .3s cubic-bezier(.4, 0, .2, 1),
    border-color .3s cubic-bezier(.4, 0, .2, 1);
}
.log-entry-error {
  border-left: 3px solid var(--danger-border);
  background: color-mix(in srgb, var(--danger-bg) 42%, var(--surface));
}
.log-entry-meta {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
  color: var(--text-subtle);
  font-family: var(--font-mono);
  font-size: 12px;
}
.log-entry-meta time {
  flex: 0 0 auto;
}
.log-source {
  overflow: hidden;
  color: var(--text-subtle);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.log-message {
  margin: 9px 0 0;
  color: var(--text);
  font-size: 14px;
  line-height: 1.6;
  white-space: pre-wrap;
}
.log-details {
  margin-top: 10px;
}
.log-details-scrollbar {
  max-height: 280px;
  margin: 9px 0 0;
}
.log-details pre {
  margin: 0;
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-sm);
  background: var(--surface-subtle);
  color: var(--text-muted);
  font-family: var(--font-mono);
  font-size: 12px;
  line-height: 1.55;
  white-space: pre-wrap;
}
.log-more-button {
  display: flex;
  width: calc(100% - 32px);
  margin: 16px;
}
.logs-empty-card { border-style: dashed; }
:deep(.logs-empty) {
  display: grid;
  min-height: 88px;
  place-items: center;
  margin: 0;
}
.logs-select-control {
  flex: 0 0 110px;
  width: 110px;
  max-width: 100%;
}
.logs-date-control {
  flex-basis: 130px;
  width: 130px;
}
@media (max-width: 767px) {
  .logs-view :deep(.logs-filters) {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
  .logs-select-control {
    width: 100%;
    min-width: 0;
  }
  .logs-search {
    grid-column: 1 / -1;
  }
  .logs-filter-button,
  .logs-refresh-button {
    width: 100%;
  }
  .log-entry {
    padding: 14px;
  }
  .log-entry-meta {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 6px 8px;
  }
  .log-level {
    width: fit-content;
  }
  .log-source {
    grid-column: 1 / -1;
    white-space: normal;
    overflow-wrap: anywhere;
  }
  .log-message {
    margin-top: 10px;
  }
}
</style>
