<script setup lang="ts">
import { useModalState } from '../../ui/useModalState';
import AppTooltip from '../../components/AppTooltip.vue';
import AppNumberInput from '../../components/AppNumberInput.vue';
import { NCard, NCollapseTransition, NFormItem, NText,
  NButton,
  NInput,
  NRadioGroup,
  NTabs,
  NTab,
  NRadio,
  NCheckboxGroup,
  NCheckbox,
  type InputInst,
} from 'naive-ui';
import { computed, nextTick, onBeforeUnmount, ref, useId, watch } from 'vue';
import { isAbortError, readApiError } from '../../api/client';
import { executeRequest } from '../../api/requestExecutor';
import { focusInvalidInput } from '../../ui/focusInvalidInput';
import AppIcon from '../../components/AppIcon.vue';
import AppModal from '../../components/AppModal.vue';
import HelpHint from '../../components/HelpHint.vue';

const formId = useId();

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
type CronField = (typeof fields)[number];
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

const createRule = (min: number, max: number): Rule => ({
  mode: 'every',
  start: min,
  end: max,
  step: 1,
  values: [],
  raw: '',
});
const isInRange = (value: unknown, min: number, max: number): value is number =>
  typeof value === 'number' && Number.isInteger(value) && value >= min && value <= max;

// 可视化控件无法表示的语法保留原文，合法性由后端校验，避免打开生成器时改写表达式。
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
    if (
      isInRange(start, field.min, field.max) &&
      isInRange(end, field.min, field.max) &&
      start <= end
    )
      return { ...rule, mode: 'range', start, end };
  }
  if (/^\d+(,\d+)*$/.test(raw)) {
    const values = [...new Set(raw.split(',').map(Number))];
    if (values.every((value) => isInRange(value, field.min, field.max)))
      return { ...rule, mode: 'values', values };
  }
  return { ...rule, mode: 'custom', raw };
}

const rules = ref<Rule[]>(fields.map((field) => createRule(field.min, field.max)));
const draftExpression = ref<string>();
const generatorOpenModal = useModalState(() => false);
const generatorOpen = generatorOpenModal.data;
const activeField = ref(0);
const currentField = computed(() => fields[activeField.value]);
const currentRule = computed(() => rules.value[activeField.value]);
const weekdays = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'];
const choices = computed(() =>
  Array.from(
    { length: currentField.value.max - currentField.value.min + 1 },
    (_, index) => currentField.value.min + index,
  ),
);
const generated = computed(
  () =>
    draftExpression.value ??
    rules.value
      .map((rule) => {
        switch (rule.mode) {
          case 'interval':
            return `${rule.start}/${rule.step}`;
          case 'range':
            return `${rule.start}-${rule.end}`;
          case 'values':
            return [...rule.values].sort((a, b) => a - b).join(',');
          case 'custom':
            return rule.raw;
          default:
            return '*';
        }
      })
      .join(' '),
);
const validation = computed<RuleValidation | undefined>(() => {
  if (draftExpression.value !== undefined) return undefined;
  const isMissing = (value: unknown) => value === '' || value === null || value === undefined;
  for (const [index, rule] of rules.value.entries()) {
    const field = fields[index];
    if (rule.mode === 'interval' || rule.mode === 'range') {
      if (!isInRange(rule.start, field.min, field.max))
        return {
          index,
          control: 'start',
          missing: isMissing(rule.start),
          message: `请填写有效的${field.label}起始值`,
        };
      if (rule.mode === 'interval') {
        if (!isInRange(rule.step, 1, field.max - field.min + 1))
          return {
            index,
            control: 'step',
            missing: isMissing(rule.step),
            message: `请填写有效的${field.label}间隔`,
          };
      } else {
        if (!isInRange(rule.end, field.min, field.max))
          return {
            index,
            control: 'end',
            missing: isMissing(rule.end),
            message: `请填写有效的${field.label}结束值`,
          };
        if (rule.start > rule.end)
          return {
            index,
            control: 'start',
            missing: false,
            message: `${field.label}起始值不能大于结束值`,
          };
      }
    }
    if (
      rule.mode === 'values' &&
      (!rule.values.length || !rule.values.every((value) => isInRange(value, field.min, field.max)))
    )
      return {
        index,
        control: 'values',
        missing: !rule.values.length,
        message: `请至少选择一个有效的${field.label}`,
      };
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
  if (showValidation.value && validation.value?.index === activeField.value)
    return validation.value.control;
  return undefined;
});
watch(
  rules,
  () => {
    showValidation.value = false;
    draftExpression.value = undefined;
  },
  { deep: true, flush: 'sync' },
);
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
      `/api/tasks/cron/preview?expression=${encodeURIComponent(expression)}`,
      {},
      controller,
      async (response) => {
        if (!response.ok) {
          return {
            nextRuns: [],
            valid: response.status === 400 ? false : undefined,
            error: await readApiError(response, '无法计算执行时间，请重试'),
          };
        }
        const data: { nextRuns: string[] } = await response.json();
        return { nextRuns: data.nextRuns, valid: true };
      },
    );
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
watch(
  [() => generatorOpenModal.show, generated, generatorError],
  () => {
    cancelPreview();
    nextRuns.value = [];
    previewError.value = '';
    previewLoading.value = false;
    expressionValid.value = undefined;
    showValidation.value = false;
    if (!generatorOpenModal.show || generatorError.value) return;
    previewLoading.value = true;
    const controller = new AbortController();
    previewController = controller;
    const expression = generated.value;
    previewTimer = setTimeout(() => loadPreview(expression, controller), 300);
  },
  { flush: 'sync' },
);
onBeforeUnmount(cancelPreview);

function openGenerator() {
  showValidation.value = false;
  const parts = props.modelValue.trim().split(/\s+/);
  rules.value = fields.map((field, index) =>
    parts.length === fields.length
      ? parseRule(parts[index], field)
      : createRule(field.min, field.max),
  );
  draftExpression.value = props.modelValue.trim() ? props.modelValue : undefined;
  activeField.value = 0;
  generatorOpenModal.open(true);
}
function updateExpression(value: string) {
  emit('update:error', '');
  emit('update:modelValue', value);
}
function updateDraftExpression(value: string) {
  draftExpression.value = value;
  showValidation.value = false;
}
function editExpression(event: Event | string, inGenerator = false) {
  if (typeof event !== 'string' && (event as InputEvent).isComposing) return;
  const value = typeof event === 'string' ? event : (event.target as HTMLInputElement).value;
  if (inGenerator) updateDraftExpression(value);
  else updateExpression(value);
}
async function applyExpression() {
  if (!generatorOpenModal.show) return;
  const invalid = validation.value;
  if (invalid) {
    showValidation.value = true;
    activeField.value = invalid.index;
    await nextTick();
    if (!generatorOpenModal.show || !showValidation.value || validation.value !== invalid) return;
    const input = rulesElement.value?.querySelector<HTMLElement>(
      `[data-cron-mode="${currentRule.value.mode}"] [data-control="${invalid.control}"] :is(input, [role="checkbox"])`,
    );
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
  generatorOpenModal.close();
}
</script>

<template>
  <div class="cron-editor">
    <NFormItem
      label="Cron 表达式"
      required
      :label-props="{ for: `${formId}-field-0` }"
      :show-feedback="Boolean(error)"
      :validation-status="error ? 'error' : undefined"
      class="form-field"
    >
      <NInput
        :value="modelValue"
        placeholder="例如：0 */5 * * * *"
        :status="Boolean(error) || undefined ? 'error' : undefined"
        @update:value="updateExpression"
        :input-props="{ id: `${formId}-field-0`, 'aria-invalid': Boolean(error) || undefined }"
      >
        <template #suffix>
          <AppTooltip text="生成 Cron 表达式">
            <NButton
              quaternary
              circle
              size="tiny"
              attr-type="button"
              aria-label="生成 Cron 表达式"
              @mousedown.prevent
              @click="openGenerator"
            >
              <AppIcon name="clock" />
            </NButton>
          </AppTooltip>
        </template>
      </NInput>
      <template #feedback><span role="alert">{{ error }}</span></template>
    </NFormItem>
    <AppModal
      :show="generatorOpenModal.show"
      @after-leave="generatorOpenModal.afterLeave"
      v-if="generatorOpen"
      class="cron-generator-modal"
      title="生成 Cron 表达式"
      size="medium"
      :dismiss-on-backdrop="false"
      @close="generatorOpenModal.close()"
    >
      <div class="cron-generator">
        <NTabs
          v-model:value="activeField"
          type="line"
          justify-content="space-evenly"
          :tab-style="{ width: '100%', justifyContent: 'center', minHeight: '44px' }"
          class="cron-generator-tabs"
          aria-label="Cron 字段"
        >
          <NTab
            v-for="(field, index) in fields"
            :key="field.label"
            :name="index"
          >
            <NButton text :type="activeField === index ? 'primary' : 'default'">{{ field.label }}</NButton>
          </NTab>
        </NTabs>
        <div ref="rulesElement" class="cron-generator-rules">
          <NRadioGroup v-model:value="currentRule.mode" class="cron-rule-options">
            <NRadio value="every">每{{ currentField.label }}</NRadio>
            <div data-cron-mode="range">
              <NRadio value="range">周期</NRadio>
              <NCollapseTransition :show="currentRule.mode === 'range'" :inert="currentRule.mode !== 'range' || undefined">
                <div class="cron-generator-range cron-rule-dependent">
                  <NFormItem
                    label="从"
                    :label-props="{ for: `${formId}-field-1` }"
                    :show-feedback="Boolean(invalidControl === 'start')"
                    :validation-status="invalidControl === 'start' ? 'error' : undefined"
                    class="form-field"
                    data-control="start"
                  >
                    <AppNumberInput
                      :id="`${formId}-field-1`"
                      v-model="currentRule.start"
                      :min="currentField.min"
                      :max="currentField.max"
                      :placeholder="`${currentField.min}–${currentField.max}`"
                      :aria-invalid="invalidControl === 'start' || undefined"
                    />
                    <template #feedback><span role="alert">{{
                      generatorError
                    }}</span></template>
                  </NFormItem>
                  <NFormItem
                    label="到"
                    :label-props="{ for: `${formId}-field-2` }"
                    :show-feedback="Boolean(invalidControl === 'end')"
                    :validation-status="invalidControl === 'end' ? 'error' : undefined"
                    class="form-field"
                    data-control="end"
                  >
                    <AppNumberInput
                      :id="`${formId}-field-2`"
                      v-model="currentRule.end"
                      :min="currentField.min"
                      :max="currentField.max"
                      :placeholder="`${currentField.min}–${currentField.max}`"
                      :aria-invalid="invalidControl === 'end' || undefined"
                    />
                    <template #feedback><span role="alert">{{
                      generatorError
                    }}</span></template>
                  </NFormItem>
                </div>
              </NCollapseTransition>
            </div>
            <div data-cron-mode="interval">
              <NRadio value="interval">间隔</NRadio>
              <NCollapseTransition :show="currentRule.mode === 'interval'" :inert="currentRule.mode !== 'interval' || undefined">
                <div class="cron-generator-range cron-rule-dependent">
                  <NFormItem
                    label="起始值"
                    :label-props="{ for: `${formId}-field-3` }"
                    :show-feedback="Boolean(invalidControl === 'start')"
                    :validation-status="invalidControl === 'start' ? 'error' : undefined"
                    class="form-field"
                    data-control="start"
                  >
                    <AppNumberInput
                      :id="`${formId}-field-3`"
                      v-model="currentRule.start"
                      :min="currentField.min"
                      :max="currentField.max"
                      :placeholder="`${currentField.min}–${currentField.max}`"
                      :aria-invalid="invalidControl === 'start' || undefined"
                    />
                    <template #feedback><span role="alert">{{
                      generatorError
                    }}</span></template>
                  </NFormItem>
                  <NFormItem
                    label="间隔"
                    :label-props="{ for: `${formId}-field-4` }"
                    :show-feedback="Boolean(invalidControl === 'step')"
                    :validation-status="invalidControl === 'step' ? 'error' : undefined"
                    class="form-field"
                    data-control="step"
                  >
                    <AppNumberInput
                      :id="`${formId}-field-4`"
                      v-model="currentRule.step"
                      min="1"
                      :max="currentField.max - currentField.min + 1"
                      :placeholder="`1–${currentField.max - currentField.min + 1}`"
                      :aria-invalid="invalidControl === 'step' || undefined"
                    />
                    <template #feedback><span role="alert">{{
                      generatorError
                    }}</span></template>
                  </NFormItem>
                </div>
              </NCollapseTransition>
            </div>
            <div data-cron-mode="values">
              <NRadio value="values">指定</NRadio>
              <NCollapseTransition :show="currentRule.mode === 'values'" :inert="currentRule.mode !== 'values' || undefined">
                <NFormItem
                  :show-label="false"
                  :show-feedback="invalidControl === 'values'"
                  :validation-status="invalidControl === 'values' ? 'error' : undefined"
                  class="form-field cron-rule-dependent"
                >
                  <NCheckboxGroup
                    :value="currentRule.values"
                    @update:value="currentRule.values = $event.map(Number)"
                    class="cron-generator-values"
                    data-control="values"
                  >
                    <NCheckbox v-for="value in choices" :key="value" :value="value">{{
                      currentField.label === '周' ? weekdays[value] : value
                    }}</NCheckbox>
                  </NCheckboxGroup>
                  <template #feedback><span role="alert">{{ generatorError }}</span></template>
                </NFormItem>
              </NCollapseTransition>
            </div>
            <p v-if="currentRule.mode === 'custom'" class="cron-custom-hint">
              当前字段使用特殊语法「{{ currentRule.raw }}」，保留原值；选择上述规则可替换
            </p>
          </NRadioGroup>
        </div>
        <NFormItem
          :label-props="{ for: expressionId }"
          :show-feedback="false"
          class="form-field"
        >
          <template #label>
            <span class="field-label-with-hint">
              <span>Cron 表达式</span>
              <HelpHint :label="specialSyntaxHelp" />
            </span>
          </template>
          <NInput
            :ref="
              (element) => {
                expressionInput = (element as InputInst | null)?.inputElRef ?? undefined;
              }
            "
            :value="generated"
            :status="showValidation && expressionValid === false ? 'error' : undefined"
            :input-props="{
              id: expressionId,
              'aria-invalid': (showValidation && expressionValid === false) || undefined,
            }"
            @update:value="editExpression($event, true)"
          />
        </NFormItem>
        <NCard embedded size="small" content-class="cron-preview" aria-live="polite" :aria-busy="previewLoading">
          <NText :depth="2">未来 5 次执行时间</NText>
          <div class="cron-preview-body">
            <NText v-if="generatorError" :depth="2">{{
              previewValidationMessage
            }}</NText>
            <NText v-else-if="previewLoading">计算中…</NText>
            <NText v-else-if="previewError" type="error" role="alert">{{
              previewError
            }}</NText>
            <ul v-else-if="nextRuns.length">
              <li v-for="time in nextRuns" :key="time">
                {{ time.replace('T', ' ').slice(0, 19) }}
              </li>
            </ul>
            <NText v-else>该表达式没有后续执行时间</NText>
          </div>
        </NCard>
      </div>
      <template #actions>
        <NButton attr-type="button" @click="generatorOpenModal.close()">取消</NButton>
        <NButton attr-type="button" @click="applyExpression" type="primary">确认</NButton>
      </template>
    </AppModal>
  </div>
</template>

<style>
.cron-editor { position: relative; margin-top: 12px; }
</style>

<style scoped>
.cron-generator-modal :deep(.modal-content) {
  padding-top: 9px;
}
.cron-generator,
.cron-generator-rules {
  display: grid;
  gap: 16px;
}
.cron-rule-options {
  display: grid;
  gap: 16px;
}
.cron-rule-dependent {
  padding-top: 16px;
}
.cron-generator-tabs :deep(.n-tabs-tab-wrapper) {
  flex: 1;
  min-width: 0;
}
.cron-generator-range {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  align-items: start;
  gap: 12px;
}
.cron-custom-hint, .cron-preview-body { transition: color .3s cubic-bezier(.4, 0, .2, 1); }
.cron-custom-hint {
  color: var(--text-muted);
}
.cron-generator-values {
  display: grid;
  width: 100%;
  min-width: 0;
  grid-template-columns: repeat(auto-fit, minmax(64px, 1fr));
  gap: 12px;
}
.cron-generator :deep(.cron-preview) {
  display: grid;
  gap: 6px;
}
.cron-preview-body {
  min-height: 9em;
  color: var(--text);
  line-height: 1.8;
}
.cron-preview ul {
  margin: 0;
  padding: 0;
  list-style: none;
  font-variant-numeric: tabular-nums;
}
</style>
