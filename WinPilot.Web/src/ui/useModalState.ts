import { ref, type Ref } from 'vue';

// 弹窗显示状态与业务数据分离：关闭先播放离场动画，afterLeave 再清空内容。
export function useModalState<T>(
  initial: () => T,
  isOpen: (value: T) => boolean = Boolean,
  onClosed?: () => void,
) {
  const data = ref(initial()) as Ref<T>;
  const visible = ref(isOpen(data.value));
  function reset() {
    visible.value = false;
    data.value = initial();
    onClosed?.();
  }
  return {
    data,
    get show() { return visible.value; },
    open(value: T) {
      if (!isOpen(value)) { visible.value = false; return; }
      data.value = value;
      visible.value = true;
    },
    close() {
      visible.value = false;
      if (!isOpen(data.value)) reset();
    },
    reset,
    afterLeave() {
      // 离场期间可能重新打开，此时旧动画回调不能清除新弹窗数据。
      if (visible.value || !isOpen(data.value)) return false;
      reset();
      return true;
    },
  };
}
