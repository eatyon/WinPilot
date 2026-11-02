import { computed, ref, watch, type Ref } from 'vue';
import type { Entry, DirectoryResult } from './fileModels';

export function useFileSelection(directory: Ref<DirectoryResult | undefined>) {
  const fileSearch = ref('');
  const page = ref(1);
  const pageSize = ref(25);
  const selectedEntries = ref<Entry[]>([]);

  const filteredEntries = computed(() => {
    const keyword = fileSearch.value.trim().toLocaleLowerCase();
    return keyword
      ? (directory.value?.entries ?? []).filter((entry) =>
          entry.name.toLocaleLowerCase().includes(keyword),
        )
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
  const selectedPaths = computed(() => new Set(selectedEntries.value.map((entry) => entry.path)));
  const selectedPageCount = computed(
    () => pagedEntries.value.filter((entry) => selectedPaths.value.has(entry.path)).length,
  );
  const allPagedEntriesSelected = computed(
    () => pagedEntries.value.length > 0 && selectedPageCount.value === pagedEntries.value.length,
  );
  const somePagedEntriesSelected = computed(
    () => selectedPageCount.value > 0 && !allPagedEntriesSelected.value,
  );

  watch([fileSearch, pageSize], () => {
    page.value = 1;
  });
  watch(pageCount, (value) => {
    if (page.value > value) page.value = value;
  });

  function normalizePageSize() {
    pageSize.value = Math.max(1, Math.floor(Number(pageSize.value) || 25));
  }

  function toggleEntrySelection(entry: Entry, checked: boolean) {
    const selected = selectedPaths.value.has(entry.path);
    if (checked && !selected) selectedEntries.value = [...selectedEntries.value, entry];
    if (!checked && selected)
      selectedEntries.value = selectedEntries.value.filter(
        (selected) => selected.path !== entry.path,
      );
  }
  // 以路径识别项目，全选只更新当前页，保留其他页的选择。
  function togglePagedSelection(checked: boolean) {
    const pagedPaths = new Set(pagedEntries.value.map((entry) => entry.path));
    selectedEntries.value = checked
      ? [
          ...selectedEntries.value.filter((entry) => !pagedPaths.has(entry.path)),
          ...pagedEntries.value,
        ]
      : selectedEntries.value.filter((entry) => !pagedPaths.has(entry.path));
  }

  return {
    fileSearch, page, pageSize, selectedEntries, filteredEntries, pageCount, pagedEntries,
    selectedPaths, selectedPageCount, allPagedEntriesSelected, somePagedEntriesSelected,
    normalizePageSize, toggleEntrySelection, togglePagedSelection,
  };
}
