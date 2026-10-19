<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, useId, watch } from 'vue';
import { isAbortError, readApiError } from '../../api/client';
import { executeRequest } from '../../api/requestExecutor';
import { focusInvalidInput } from '../../ui/focusInvalidInput';
import AppIcon from '../../components/AppIcon.vue';
import AppModal from '../../components/AppModal.vue';
import HelpHint from '../../components/HelpHint.vue';

const props = defineProps<{ modelValue: string; error?: string }>();
const emit = defineEmits<{ 'update:modelValue': [string]; 'update:error': [string] }>();
const expressionId = useId();
const specialSyntaxHelp = [
  '特殊语法',
  '日：L 每月最后一天；L-1 倒数第二天',
  '日：15W 距每月 15 日最近的工作日；LW 每月最后一个工作日',
  '周：1#2 每月第二个周一；5L 每月最后一个周五',
  '范围间隔：10-30/5 在 10 至 30 之间每隔 5 个单位',
  '工作日指周一至周五，不考虑法定节假日',
  '特殊语法可在下方直接编辑；未修改规则时保留原值',
].join('\n');
const fields = [
  { label: '秒', min: 0, max: 59 },
  { label: '分钟', min: 0, max: 59 },
  { label: '小时', min: 0, max: 23 },
  { label: '日', min: 1, max: 31 },
  { label: '月', min: 1, max: 12 },
  { label: '周', min: 0, max: 6 },
];
type CronField = typeof fields[number];
type Rule = {
  mode: 'every' | 'interval' | 'range' | 'values' | 'custom';
  start: number | '';
  end: number | '';
  step: number | '';
  values: number[];
  raw: string;
};
type RuleValidation = {
  index: number;
  control: 'start' | 'end' | 'step' | 'values';
  missing: boolean;
  message: string;
};
type PreviewResult = { nextRuns: string[]; valid?: boolean; error?: string };

const createRule = (min: number, max: number): Rule => ({ mode: 'every', start: min, end: max, step: 1, values: [], raw: '' });
const isInRange = (value: unknown, min: number, max: number): value is number =>
  typeof value === 'number' && Number.isInteger(value) && value >= min && value <= max;

function parseRule(raw: string, field: CronField): Rule {
  const rule = createRule(field.min, field.max);
  if (raw === '*') return rule;
  const interval = raw.match(/^(\*|\d+)\/(\d+)$/);
  if (interval) {
    const start = interval[1] === '*' ? field.min : Number(interval[1]);
    const step = Number(interval[2]);
    if (isInRange(start, field.min, field.max) && isInRange(step, 1, field.max - field.min + 1))
      return { ...rule, mode: 'interval', start, step };
  }
  const range = raw.match(/^(\d+)-(\d+)$/);
  if (range) {
    const start = Number(range[1]);
    const end = Number(range[2]);
    if (isInRange(start, field.min, field.max) && isInRange(end, field.min, field.max) && start <= end)
      return { ...rule, mode: 'range', start, end };
  }
  if (/^\d+(,\d+)*$/.test(raw)) {
    const values = [...new Set(raw.split(',').map(Number))];
    if (values.every(value => isInRange(value, field.min, field.max)))
      return { ...rule, mode: 'values', values };
  }
  return { ...rule, mode: 'custom', raw };
}

const rules = ref<Rule[]>(fields.map(field => createRule(field.min, field.max)));
const draftExpression = ref<string>();
const generatorOpen = ref(false);
const activeField = ref(0);
const currentField = computed(() => fields[activeField.value]);
const currentRule = computed(() => rules.value[activeField.value]);
const weekdays = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'];
const choices = computed(() => Array.from({ length: currentField.value.max - currentField.value.min + 1 }, (_, index) => currentField.value.min + index));
const generated = computed(() => draftExpression.value ?? rules.value.map(rule => {
  switch (rule.mode) {
    case 'interval': return `${rule.start}/${rule.step}`;
    case 'range': return `${rule.start}-${rule.end}`;
    case 'values': return [...rule.values].sort((a, b) => a - b).join(',');
    case 'custom': return rule.raw;
    default: return '*';
  }
}).join(' '));
const validation = computed<RuleValidation | undefined>(() => {
  if (draftExpression.value !== undefined) return undefined;
  const isMissing = (value: unknown) => value === '' || value === null || value === undefined;
  for (const [index, rule] of rules.value.entries()) {
    const field = fields[index];
    if (rule.mode === 'interval' || rule.mode === 'range') {
      if (!isInRange(rule.start, field.min, field.max))
        return { index, control: 'start', missing: isMissing(rule.start), message: `请填写有效的${field.label}起始值` };
      if (rule.mode === 'interval') {
        if (!isInRange(rule.step, 1, field.max - field.min + 1))
          return { index, control: 'step', missing: isMissing(rule.step), message: `请填写有效的${field.label}间隔` };
      } else {
        if (!isInRange(rule.end, field.min, field.max))
          return { index, control: 'end', missing: isMissing(rule.end), message: `请填写有效的${field.label}结束值` };
        if (rule.start > rule.end)
          return { index, control: 'start', missing: false, message: `${field.label}起始值不能大于结束值` };
      }
    }
    if (rule.mode === 'values' && (!rule.values.length || !rule.values.every(value => isInRange(value, field.min, field.max))))
      return { index, control: 'values', missing: !rule.values.length, message: `请至少选择一个有效的${field.label}` };
  }
  return undefined;
});
const generatorError = computed(() => validation.value?.message ?? '');
const previewValidationMessage = computed(() => {
  if (!validation.value) return '';
  return validation.value.missing ? '表达式不完整，无法预览' : '表达式无效，无法预览';
});
const showValidation = ref(false);
const rulesElement = ref<HTMLElement>();
const expressionInput = ref<HTMLInputElement>();
const invalidControl = computed(() => {
  if (showValidation.value && validation.value?.index === activeField.value) return validation.value.control;
  return undefined;
});
watch(rules, () => {
  showValidation.value = false;
  draftExpression.value = undefined;
}, { deep: true, flush: 'sync' });
const nextRuns = ref<string[]>([]);
const previewError = ref('');
const previewLoading = ref(false);
const expressionValid = ref<boolean>();
let previewTimer: ReturnType<typeof setTimeout> | undefined;
let previewController: AbortController | undefined;

function cancelPreview() {
  clearTimeout(previewTimer);
  previewTimer = undefined;
  previewController?.abort();
  previewController = undefined;
}
async function loadPreview(expression: string, controller: AbortController) {
  let result: PreviewResult;
  try {
    result = await executeRequest<PreviewResult>(
      `/api/tasks/cron/preview?expression=${encodeURIComponent(expression)}`, {}, controller,
      async response => {
        if (!response.ok) {
          return {
            nextRuns: [],
            valid: response.status === 400 ? false : undefined,
            error: await readApiError(response, '无法计算执行时间，请重试'),
          };
        }
        const data: { nextRuns: string[] } = await response.json();
        return { nextRuns: data.nextRuns, valid: true };
      });
  } catch (error) {
    result = { nextRuns: [] };
    if (!isAbortError(error))
      result.error = error instanceof Error ? error.message : '无法计算执行时间，请重试';
  }
  if (previewController !== controller) return;
  nextRuns.value = result.nextRuns;
  expressionValid.value = result.valid;
  previewError.value = result.error ?? '';
  previewLoading.value = false;
}
watch([generatorOpen, generated, generatorError], () => {
  cancelPreview();
  nextRuns.value = [];
  previewError.value = '';
  previewLoading.value = false;
  expressionValid.value = undefined;
  showValidation.value = false;
  if (!generatorOpen.value || generatorError.value) return;
  previewLoading.value = true;
  const controller = new AbortController();
  previewController = controller;
  const expression = generated.value;
  previewTimer = setTimeout(() => loadPreview(expression, controller), 300);
}, { flush: 'sync' });
onBeforeUnmount(cancelPreview);

function openGenerator() {
  showValidation.value = false;
  const parts = props.modelValue.trim().split(/\s+/);
  rules.value = fields.map((field, index) => parts.length === fields.length
    ? parseRule(parts[index], field)
    : createRule(field.min, field.max));
  draftExpression.value = props.modelValue.trim() ? props.modelValue : undefined;
  activeField.value = 0;
  generatorOpen.value = true;
}
function updateExpression(value: string) {
  emit('update:error', '');
  emit('update:modelValue', value);
}
function updateDraftExpression(value: string) {
  draftExpression.value = value;
  showValidation.value = false;
}
function editExpression(event: Event, inGenerator = false) {
  if ((event as InputEvent).isComposing) return;
  const value = (event.target as HTMLInputElement).value;
  if (inGenerator) updateDraftExpression(value);
  else updateExpression(value);
}
async function applyExpression() {
  if (!generatorOpen.value) return;
  const invalid = validation.value;
  if (invalid) {
    showValidation.value = true;
    activeField.value = invalid.index;
    await nextTick();
    if (!generatorOpen.value || !showValidation.value || validation.value !== invalid) return;
    const input = rulesElement.value?.querySelector<HTMLInputElement>(`[data-control="${invalid.control}"] input`);
    focusInvalidInput(input);
    return;
  }
  if (previewLoading.value || expressionValid.value !== true) {
    if (expressionValid.value === false) {
      showValidation.value = true;
      focusInvalidInput(expressionInput.value);
    }
    return;
  }
  updateExpression(generated.value);
  generatorOpen.value = false;
}
</script>

<template>
  <div class="cron-editor">
    <label class="field-control">
      <span>Cron 表达式 *</span>
      <div class="input-action">
        <input :value="modelValue" placeholder="例如：0 */5 * * * *"
          :aria-invalid="Boolean(error) || undefined"
          @input="editExpression($event)" @compositionend="editExpression($event)" />
        <button class="input-action-button" type="button" title="生成 Cron 表达式" aria-label="生成 Cron 表达式" @click="openGenerator">
          <AppIcon name="clock" />
        </button>
      </div>
      <small v-if="error" class="field-error" role="alert">{{ error }}</small>
    </label>
    <AppModal v-if="generatorOpen" class="cron-generator-modal" title="生成 Cron 表达式" size="medium" :dismiss-on-backdrop="false" @close="generatorOpen = false">
      <div class="cron-generator">
        <nav class="cron-generator-tabs" aria-label="Cron 字段">
          <button v-for="(field, index) in fields" :key="field.label" class="cron-generator-tab" :class="{ active: activeField === index }"
            type="button" :aria-pressed="activeField === index" @click="activeField = index">{{ field.label }}</button>
        </nav>
        <div ref="rulesElement" class="cron-generator-rules">
          <label><input v-model="currentRule.mode" type="radio" value="every" />每{{ currentField.label }}</label>
          <label><input v-model="currentRule.mode" type="radio" value="range" />周期</label>
          <div v-if="currentRule.mode === 'range'" class="cron-generator-range">
            <label class="field-control" data-control="start">
              <span>从</span>
              <input v-model.number="currentRule.start" type="number"
                :min="currentField.min" :max="currentField.max" :placeholder="`${currentField.min}–${currentField.max}`"
                :aria-invalid="invalidControl === 'start' || undefined" />
              <small v-if="invalidControl === 'start'" class="field-error" role="alert">{{ generatorError }}</small>
            </label>
            <label class="field-control" data-control="end">
              <span>到</span>
              <input v-model.number="currentRule.end" type="number"
                :min="currentField.min" :max="currentField.max" :placeholder="`${currentField.min}–${currentField.max}`"
                :aria-invalid="invalidControl === 'end' || undefined" />
              <small v-if="invalidControl === 'end'" class="field-error" role="alert">{{ generatorError }}</small>
            </label>
          </div>
          <label><input v-model="currentRule.mode" type="radio" value="interval" />间隔</label>
          <div v-if="currentRule.mode === 'interval'" class="cron-generator-range">
            <label class="field-control" data-control="start">
              <span>起始值</span>
              <input v-model.number="currentRule.start" type="number"
                :min="currentField.min" :max="currentField.max" :placeholder="`${currentField.min}–${currentField.max}`"
                :aria-invalid="invalidControl === 'start' || undefined" />
              <small v-if="invalidControl === 'start'" class="field-error" role="alert">{{ generatorError }}</small>
            </label>
            <label class="field-control" data-control="step">
              <span>间隔</span>
              <input v-model.number="currentRule.step" type="number" min="1"
                :max="currentField.max - currentField.min + 1" :placeholder="`1–${currentField.max - currentField.min + 1}`"
                :aria-invalid="invalidControl === 'step' || undefined" />
              <small v-if="invalidControl === 'step'" class="field-error" role="alert">{{ generatorError }}</small>
            </label>
          </div>
          <label><input v-model="currentRule.mode" type="radio" value="values" />指定</label>
          <div v-if="currentRule.mode === 'values'" class="cron-generator-values" data-control="values">
            <label v-for="value in choices" :key="value"><input v-model="currentRule.values" type="checkbox" :value="value" />{{ currentField.label === '周' ? weekdays[value] : value }}</label>
          </div>
          <small v-if="invalidControl === 'values'" class="field-error" role="alert">{{ generatorError }}</small>
          <p v-if="currentRule.mode === 'custom'" class="cron-custom-hint">当前字段使用特殊语法「{{ currentRule.raw }}」，保留原值；选择上述规则可替换</p>
        </div>
        <div class="field-control">
          <span class="cron-expression-label">
            <label :for="expressionId">Cron 表达式</label>
            <HelpHint :label="specialSyntaxHelp" />
          </span>
          <input :id="expressionId" ref="expressionInput" :value="generated"
            :aria-invalid="(showValidation && expressionValid === false) || undefined"
            @input="editExpression($event, true)" @compositionend="editExpression($event, true)" />
        </div>
        <div class="cron-preview" aria-live="polite" :aria-busy="previewLoading">
          <span class="cron-preview-title">未来 5 次执行时间</span>
          <div class="cron-preview-body">
            <small v-if="generatorError" class="cron-preview-title">{{ previewValidationMessage }}</small>
            <small v-else-if="previewLoading">计算中…</small>
            <small v-else-if="previewError" class="field-error" role="alert">{{ previewError }}</small>
            <ul v-else-if="nextRuns.length"><li v-for="time in nextRuns" :key="time">{{ time.replace('T', ' ').slice(0, 19) }}</li></ul>
            <small v-else>该表达式没有后续执行时间</small>
          </div>
        </div>
      </div>
      <template #actions>
        <button class="button button-secondary" type="button" @click="generatorOpen = false">取消</button>
        <button class="button button-primary" type="button" @click="applyExpression">确认</button>
      </template>
    </AppModal>
  </div>
</template>

<style scoped>
.cron-generator-modal :deep(.modal-content) { padding-top: 9px; }
.cron-generator, .cron-generator-rules { display: grid; gap: 16px; }
.cron-generator-tabs { display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); border-bottom: 1px solid var(--border); }
.cron-generator-tab { position: relative; min-width: 0; min-height: 40px; padding: 0 8px; border: 0; border-radius: 0; background: transparent; color: var(--text-muted); font-size: 14px; font-weight: 600; white-space: nowrap; }
.cron-generator-tab:hover { color: var(--text); }
.cron-generator-tab.active { color: var(--accent-text); }
.cron-generator-tab.active::after { position: absolute; bottom: 0; left: calc(50% - 10px); width: 20px; height: 2px; background: var(--accent-solid); content: ''; }
.cron-generator-range { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); align-items: start; gap: 12px; }
.cron-generator .field-control input { min-width: 0; width: 100%; }
.cron-custom-hint { color: var(--text-muted); }
.cron-expression-label { display: inline-flex; align-items: center; gap: 6px; }
.cron-expression-label :deep(.help-hint-popover) { white-space: pre-line; }
.cron-generator-rules > label, .cron-generator-values label { display: inline-flex; align-items: center; gap: 6px; }
.cron-generator-values { display: grid; grid-template-columns: repeat(auto-fit, minmax(64px, 1fr)); gap: 12px; }
.cron-preview { display: grid; gap: 6px; padding: 12px; border-radius: var(--radius-sm); background: var(--surface-subtle); font-size: 13px; }
.cron-preview-title { color: var(--text-muted); }
.cron-preview-body { min-height: 9em; color: var(--text); line-height: 1.8; }
.cron-preview ul { margin: 0; padding: 0; list-style: none; font-variant-numeric: tabular-nums; }
</style>
