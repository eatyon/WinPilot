<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { readApiError, apiFetch, isAbortError, userErrorMessage } from '../../api/client';
import AppSelect from '../../components/AppSelect.vue';
import AppMultiSelect from '../../components/AppMultiSelect.vue';

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
const selectedLevels = ref<string[]>(levelOptions.filter((level) => level !== 'DBG'));
const levelLabel = computed(() =>
  selectedLevels.value.length ? `已选择 ${selectedLevels.value.length} 项` : '未选择',
);
const keyword = ref('');
const entries = ref<LogEntry[]>([]);
const hasMore = ref(false);
const isLoading = ref(false);
const pageError = ref('');
const hasLogs = computed(() => dates.value.length > 0);
let isPageActive = false;
let loadController: AbortController | undefined;
let loadedQuery = '';
let snapshot: number | undefined;

onMounted(() => {
  isPageActive = true;
  void refreshLogs();
});
onBeforeUnmount(() => {
  isPageActive = false;
  loadController?.abort();
  loadController = undefined;
  isLoading.value = false;
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
  const result = await response.json() as T;
  ensureActive(signal);
  return result;
}
async function load(action: (signal: AbortSignal) => Promise<void>) {
  if (!isPageActive) return;
  loadController?.abort();
  const controller = new AbortController();
  loadController = controller;
  isLoading.value = true;
  pageError.value = '';
  try {
    await action(controller.signal);
  } catch (exception) {
    if (isPageActive && loadController === controller && !controller.signal.aborted && !isAbortError(exception))
      pageError.value = userErrorMessage(exception, '无法读取日志，请稍后重试');
  } finally {
    if (loadController === controller) {
      loadController = undefined;
      isLoading.value = false;
    }
  }
}
function toggleLevel(level: string, checked: boolean) {
  if (!levelOptions.some(value => value === level)) return;
  selectedLevels.value = checked
    ? [...new Set([...selectedLevels.value, level])]
    : selectedLevels.value.filter(value => value !== level);
  void loadEntries(true);
}
async function refreshLogs() {
  await load(async signal => {
    const result = await readJson<Array<{ date: string }>>('/api/logs/dates', signal);
    ensureActive(signal);
    dates.value = result.map(item => item.date);
    if (!dates.value.includes(selectedDate.value)) selectedDate.value = dates.value[0] ?? '';
    await readEntries(signal, true);
  });
}
async function loadEntries(reset: boolean) {
  if (!reset && (isLoading.value || !hasMore.value)) return;
  await load(signal => readEntries(signal, reset));
}
async function readEntries(signal: AbortSignal, reset: boolean) {
  if (!selectedDate.value || !selectedLevels.value.length) {
    clearEntries();
    return;
  }
  const query = new URLSearchParams({ date: selectedDate.value, limit: '500' });
  query.set('level', selectedLevels.value.flatMap(level => level === 'ERR' ? ['ERR', 'FTL'] : [level]).join(','));
  if (keyword.value.trim()) query.set('keyword', keyword.value.trim());
  const queryKey = query.toString();
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
</script>

<template>
  <section class="logs-view">
    <div class="logs-filters" aria-label="日志筛选">
      <label class="field-control">
        <span>日志日期</span>
        <AppSelect
          v-model="selectedDate"
          :options="dates.map((date) => ({ value: date, label: date }))"
          :disabled="isLoading || !hasLogs"
          @change="loadEntries(true)"
        />
      </label>
      <div class="field-control logs-level-control">
        <span id="logs-level-label">日志级别</span>
        <AppMultiSelect
          :model-value="selectedLevels"
          :options="levelOptions.map((level) => ({ value: level, label: level }))"
          :label="levelLabel"
          :disabled="isLoading || !hasLogs"
          labelled-by="logs-level-label"
          @toggle="toggleLevel"
        />
      </div>
      <label class="field-control logs-search">
        <span>关键词</span>
        <input
          v-model="keyword"
          type="search"
          placeholder="搜索正文或来源"
          :disabled="isLoading || !hasLogs"
          @keyup.enter="loadEntries(true)"
        />
      </label>
      <button
        class="button button-secondary logs-filter-button"
        type="button"
        :disabled="isLoading || !hasLogs"
        @click="loadEntries(true)"
      >
        筛选
      </button>
      <button
        class="button button-primary logs-refresh-button"
        type="button"
        :disabled="isLoading"
        @click="refreshLogs"
      >
        {{ isLoading ? '正在刷新…' : '刷新' }}
      </button>
    </div>
    <p v-if="pageError" class="status-message error" role="alert">{{ pageError }}</p>
    <div v-if="!hasLogs && !isLoading && !pageError" class="logs-empty">暂无日志</div>
    <div v-else-if="entries.length" class="log-list">
      <article
        v-for="entry in entries"
        :key="`${selectedDate}-${entry.id}`"
        class="log-entry"
        :class="{ 'log-entry-error': entry.level === 'ERR' || entry.level === 'FTL' }"
      >
        <div class="log-entry-meta">
          <time>{{ entry.time }}</time>
          <span class="log-level" :class="`log-level-${entry.level.toLowerCase()}`">{{ entry.level }}</span>
          <span class="log-source">{{ entry.source }}</span>
        </div>
        <p class="log-message">{{ entry.message }}</p>
        <details v-if="entry.details" class="log-details">
          <summary>查看异常详情</summary>
          <pre>{{ entry.details }}</pre>
        </details>
      </article>
      <button
        v-if="hasMore"
        class="button button-secondary log-more-button"
        type="button"
        :disabled="isLoading || !hasLogs"
        @click="loadEntries(false)"
      >
        加载更早的日志
      </button>
    </div>
    <p v-else-if="!isLoading && !pageError" class="logs-empty">没有符合条件的日志</p>
  </section>
</template>

<style scoped>
.logs-view { display: grid; gap: 18px; }
.logs-filters { display: flex; flex-wrap: wrap; align-items: end; gap: 14px; padding: 16px; border: 1px solid var(--border); border-radius: var(--radius-lg); background: var(--surface); }
.logs-search { flex: 1 1 220px; }
.logs-search input { width: 100%; }
.logs-filter-button, .logs-refresh-button { flex: 0 0 auto; }
.log-list { border: 1px solid var(--border); border-radius: var(--radius-lg); background: var(--surface); overflow: hidden; }
.log-entry { padding: 16px 18px; border-bottom: 1px solid var(--border); }
.log-entry:last-child { border-bottom: 0; }
.log-entry-error { border-left: 3px solid var(--danger-border); background: color-mix(in srgb, var(--danger-bg) 42%, var(--surface)); }
.log-entry-meta { display: flex; align-items: center; gap: 10px; min-width: 0; color: var(--text-subtle); font-family: var(--font-mono); font-size: 12px; }
.log-entry-meta time { flex: 0 0 auto; }
.log-level { padding: 2px 6px; border-radius: var(--radius-sm); background: var(--surface-subtle); color: var(--text-muted); font-family: inherit; font-weight: 700; }
.log-level-err, .log-level-ftl { background: var(--danger-bg); color: var(--danger-text); }
.log-level-wrn { color: var(--warning-text); }
.log-source { overflow: hidden; color: var(--text-subtle); text-overflow: ellipsis; white-space: nowrap; }
.log-message { margin: 9px 0 0; color: var(--text); font-size: 14px; line-height: 1.6; white-space: pre-wrap; }
.log-details { margin-top: 10px; }
.log-details summary { color: var(--danger-text); cursor: pointer; font-size: 13px; }
.log-details pre { max-height: 280px; margin: 9px 0 0; padding: 12px; overflow: auto; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface-subtle); color: var(--text-muted); font-family: var(--font-mono); font-size: 12px; line-height: 1.55; white-space: pre-wrap; }
.log-more-button { display: flex; width: calc(100% - 32px); margin: 16px; }
.logs-empty { display: grid; min-height: 180px; place-items: center; margin: 0; border: 1px dashed var(--border-strong); border-radius: var(--radius-lg); color: var(--text-muted); font-size: 14px; }
.logs-level-control { flex: 0 0 110px; width: 110px; max-width: 100%; }
@media (max-width: 767px) {
  .logs-filters { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .logs-level-control { width: 100%; min-width: 0; }
  .logs-search { grid-column: 1 / -1; }
  .logs-filter-button, .logs-refresh-button { width: 100%; }
  .log-entry { padding: 14px; }
  .log-entry-meta { display: grid; grid-template-columns: auto 1fr; gap: 6px 8px; }
  .log-level { width: fit-content; }
  .log-source { grid-column: 1 / -1; white-space: normal; overflow-wrap: anywhere; }
  .log-message { margin-top: 10px; }
}
</style>
