import { computed, ref, watch, type Ref } from 'vue';
import type { RunningProgram, ProgramGroup } from './programModels';

// 展示层仅按后端给出的 groupId 聚合，再进行搜索和分页，不重新推断进程归属。
export function useProgramList(programs: Ref<RunningProgram[]>) {
  const programSearch = ref('');
  const pageSize = ref(10);
  const currentPage = ref(1);
  const expandedGroups = ref<string[]>([]);

  const groups = computed<ProgramGroup[]>(() => {
    const grouped = new Map<string, ProgramGroup>();
    for (const process of programs.value) {
      const group = grouped.get(process.groupId);
      if (group) group.processes.push(process);
      else grouped.set(process.groupId, {
        id: process.groupId,
        name: process.groupName,
        processName: process.groupProcessName,
        processes: [process],
      });
    }
    return [...grouped.values()]
      .sort((left, right) => left.name.localeCompare(right.name) || left.id.localeCompare(right.id));
  });
  const filteredGroups = computed(() => {
    const keyword = programSearch.value.trim().toLocaleLowerCase();
    return keyword
      ? groups.value.filter((group) => group.name.toLocaleLowerCase().includes(keyword)
        || group.processes.some((process) => process.name.toLocaleLowerCase().includes(keyword)
          || process.displayName.toLocaleLowerCase().includes(keyword)
          || process.windowTitles.some((title) => title.toLocaleLowerCase().includes(keyword))))
      : groups.value;
  });
  const pageCount = computed(() => Math.max(1, Math.ceil(filteredGroups.value.length / pageSize.value)));
  const pagedGroups = computed(() =>
    filteredGroups.value.slice((currentPage.value - 1) * pageSize.value, currentPage.value * pageSize.value),
  );
  const resultSummary = computed(() =>
    programSearch.value.trim()
      ? `搜索：${filteredGroups.value.reduce((total, group) => total + group.processes.length, 0)} 个进程 · ${filteredGroups.value.length} 个进程组`
      : `查询：${programs.value.length} 个进程 · ${groups.value.length} 个进程组`,
  );

  watch([programSearch, pageSize], () => {
    currentPage.value = 1;
  });
  watch([currentPage, pageSize], () => {
    expandedGroups.value = [];
  });
  watch(pageCount, (count) => {
    if (currentPage.value > count) currentPage.value = count;
  });

  return {
    programSearch, pageSize, currentPage, expandedGroups,
    groups, filteredGroups, pageCount, pagedGroups, resultSummary,
  };
}
