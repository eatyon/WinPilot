import { h, type VNodeChild } from 'vue';
import { NEllipsis, type SelectOption, type SelectRenderLabel } from 'naive-ui';
import { textPopoverStyle } from './textPopoverStyle.ts';

export const renderSelectLabel: SelectRenderLabel = (option, selected) => {
  const label = option.label;
  if (typeof label === 'function')
    return h('span', null, [(label as (entry: unknown, selected: boolean) => VNodeChild)(option, selected)]);
  const text = String(label ?? option.value ?? '');
  const tooltip = {
    disabled: !window.matchMedia('(hover: hover) and (pointer: fine)').matches,
    style: textPopoverStyle,
  };
  return h(NEllipsis, {
    style: { maxWidth: '100%', verticalAlign: 'bottom' },
    tooltip,
  }, { default: () => text });
};

export function withSelectLabels(options: readonly SelectOption[]): SelectOption[] {
  return options.map(option => ({
    ...option,
    label: (_entry, selected) => renderSelectLabel(option, selected),
  }));
}

export function selectFallbackOption(value: string | number): SelectOption {
  return withSelectLabels([{ value, label: String(value) }])[0]!;
}
