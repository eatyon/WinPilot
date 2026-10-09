<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import AppSelect from '../../components/AppSelect.vue';
import HelpHint from '../../components/HelpHint.vue';

const props = defineProps<{ modelValue: string; error?: string }>();
const emit = defineEmits<{ 'update:modelValue': [string] }>();
type Frequency = 'minute' | 'hour' | 'day' | 'week' | 'month';
const mode = ref<'simple' | 'advanced'>('simple');
const frequency = ref<Frequency>('day');
const minute = ref(30);
const hour = ref(8);
const interval = ref(1);
const weekday = ref(1);
const monthDay = ref(1);
const weekdays = ['周一', '周二', '周三', '周四', '周五', '周六', '周日'];
const generated = computed(() => {
  switch (frequency.value) {
    case 'minute':
      return interval.value === 1 ? '* * * * *' : `*/${interval.value} * * * *`;
    case 'hour':
      return interval.value === 1 ? `${minute.value} * * * *` : `${minute.value} */${interval.value} * * *`;
    case 'week':
      return `${minute.value} ${hour.value} * * ${weekday.value}`;
    case 'month':
      return `${minute.value} ${hour.value} ${monthDay.value} * *`;
    default:
      return `${minute.value} ${hour.value} * * *`;
  }
});
function emitGenerated() {
  if (mode.value === 'simple') emit('update:modelValue', generated.value);
}
function parse(value: string) {
  const parts = value.trim().split(/\s+/);
  if (parts.length !== 5) {
    mode.value = 'advanced';
    return;
  }
  const [m, h, d, month, w] = parts;
  const minuteMatch = m.match(/^\*\/(\d+)$/);
  const hourMatch = h.match(/^\*\/(\d+)$/);
  if (h === '*' && d === '*' && month === '*' && w === '*' && (m === '*' || minuteMatch)) {
    frequency.value = 'minute';
    interval.value = minuteMatch ? Number(minuteMatch[1]) : 1;
    return;
  }
  if (d === '*' && month === '*' && w === '*' && /^\d+$/.test(m) && (h === '*' || hourMatch)) {
    frequency.value = 'hour';
    minute.value = Number(m);
    interval.value = hourMatch ? Number(hourMatch[1]) : 1;
    return;
  }
  if (d === '*' && month === '*' && w === '*' && /^\d+$/.test(m) && /^\d+$/.test(h)) {
    frequency.value = 'day';
    minute.value = Number(m);
    hour.value = Number(h);
    return;
  }
  if (d === '*' && month === '*' && /^\d+$/.test(m) && /^\d+$/.test(h) && /^\d+$/.test(w)) {
    frequency.value = 'week';
    minute.value = Number(m);
    hour.value = Number(h);
    weekday.value = Number(w) === 0 ? 7 : Number(w);
    return;
  }
  if (month === '*' && w === '*' && /^\d+$/.test(m) && /^\d+$/.test(h) && /^\d+$/.test(d)) {
    frequency.value = 'month';
    minute.value = Number(m);
    hour.value = Number(h);
    monthDay.value = Number(d);
    return;
  }
  mode.value = 'advanced';
}
let initialized = false;
watch(
  () => props.modelValue,
  (value) => {
    if (!initialized) {
      initialized = true;
      if (value) parse(value);
    }
  },
  { immediate: true },
);
watch(mode, (value) => {
  if (value === 'simple') {
    parse(props.modelValue);
    if (mode.value === 'advanced') {
      mode.value = 'simple';
      frequency.value = 'day';
      minute.value = 30;
      hour.value = 8;
      interval.value = 1;
      weekday.value = 1;
      monthDay.value = 1;
    }
    emitGenerated();
  }
});
watch([frequency, minute, hour, interval, weekday, monthDay], emitGenerated, {
  immediate: true,
});
</script>
<template>
  <div class="cron-editor">
    <div class="cron-mode">
      <label>
        <input v-model="mode" type="radio" value="simple" />
        简单设置
      </label>
      <label>
        <input v-model="mode" type="radio" value="advanced" />
        高级 Cron
      </label>
    </div>
    <div v-if="mode === 'simple'" class="cron-simple">
      <label class="field-control">
        <span>执行频率</span>
        <AppSelect
          v-model="frequency"
          :options="[
            { value: 'minute', label: '每分钟' },
            { value: 'hour', label: '每小时' },
            { value: 'day', label: '每天' },
            { value: 'week', label: '每周' },
            { value: 'month', label: '每月' },
          ]"
        />
      </label>
      <label v-if="frequency === 'minute' || frequency === 'hour'" class="field-control">
        <span>每</span>
        <input v-model.number="interval" type="number" min="1" :max="frequency === 'hour' ? 23 : 59" />
        <small>{{ frequency === 'minute' ? '分钟' : '小时' }}</small>
      </label>
      <div v-if="frequency === 'day' || frequency === 'week' || frequency === 'month'" class="cron-time">
        <label class="field-control">
          <span>时间</span>
          <div>
            <input v-model.number="hour" type="number" min="0" max="23" />
            <span>:</span>
            <input v-model.number="minute" type="number" min="0" max="59" />
          </div>
        </label>
      </div>
      <label v-if="frequency === 'hour'" class="field-control">
        <span>分钟</span>
        <input v-model.number="minute" type="number" min="0" max="59" />
      </label>
      <label v-if="frequency === 'week'" class="field-control">
        <span>星期</span>
        <AppSelect
          v-model="weekday"
          :options="weekdays.map((name, index) => ({ value: index + 1, label: name }))"
        />
      </label>
      <label v-if="frequency === 'month'" class="field-control">
        <span>日期</span>
        <AppSelect
          v-model="monthDay"
          :options="
            Array.from({ length: 31 }, (_, index) => ({ value: index + 1, label: `${index + 1} 日` }))
          "
        />
      </label>
    </div>
    <template v-else>
      <label class="field-control">
        <span class="cron-expression-label">
          Cron 表达式
          <HelpHint label="在线生成 Cron" href="https://cron.ciding.cc/" />
        </span>
        <input
          :value="modelValue"
          @input="emit('update:modelValue', ($event.target as HTMLInputElement).value)"
          placeholder="例如：*/5 * * * *"
        />
      </label>
    </template>
    <small v-if="error" class="field-error">{{ error }}</small>
  </div>
</template>
