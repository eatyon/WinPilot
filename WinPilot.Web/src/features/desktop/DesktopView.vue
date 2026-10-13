<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { apiFetch, isAbortError, userErrorMessage } from '../../api/client';
import KeyboardKeyPicker from './KeyboardKeyPicker.vue';
import AppIcon from '../../components/AppIcon.vue';
import AppSelect from '../../components/AppSelect.vue';
import { safeModeEnabled, safeModeMessage } from '../security/safeMode';
import { showToast } from '../../ui/toast';
import { getPageScroller, lockScroll } from '../../ui/scrollLock';
import { manageModalFocus } from '../../ui/modalFocus';
import { manageModalTouch } from '../../ui/modalTouch';

type IntervalOption = '1' | '3' | '5' | '10' | 'custom';

const imageUrl = ref<string>();
const isImageViewerOpen = ref(false);
const imageViewer = ref<HTMLElement>();
const imageViewerImage = ref<HTMLImageElement>();
const imageViewerScale = ref(1);
const imageViewerOffsetX = ref(0);
const imageViewerOffsetY = ref(0);
const capturedAt = ref<Date>();
const screenshotError = ref<string>();
const isCapturing = ref(false);
const autoRefreshEnabled = ref(false);
const intervalOption = ref<IntervalOption>('5');
const customIntervalSeconds = ref(15);
const keyboardEnabled = ref(false);
const selectedKeys = ref<string[]>([]);
const isSendingKeys = ref(false);
const keyboardError = ref<string>();
const keyboardStatus = ref<string>();
let autoCaptureTimer: number | undefined;
let relativeTimeTimer: number | undefined;
let lastCaptureFailureAt: number | undefined;
let screenshotController: AbortController | undefined;
let keyboardController: AbortController | undefined;
let isPageActive = false;
let releaseImageViewerScroll: (() => void) | undefined;
const currentTime = ref(Date.now());
const imageViewerPointers = new Map<number, { x: number; y: number }>();
let imageViewerPanPointerId: number | undefined;
let imageViewerPanStartX = 0;
let imageViewerPanStartY = 0;
let imageViewerPanOffsetX = 0;
let imageViewerPanOffsetY = 0;
let imageViewerPinchDistance = 0;
let imageViewerPinchScale = 1;

const isCustomIntervalValid = computed(
  () =>
    Number.isInteger(customIntervalSeconds.value) &&
    customIntervalSeconds.value >= 1 &&
    customIntervalSeconds.value <= 600,
);
const activeIntervalMilliseconds = computed(() => {
  if (!autoRefreshEnabled.value) return 0;
  const seconds =
    intervalOption.value === 'custom' ? customIntervalSeconds.value : Number(intervalOption.value);
  return intervalOption.value === 'custom' && !isCustomIntervalValid.value ? 0 : seconds * 1000;
});
const relativeCapturedAt = computed(() => {
  if (!capturedAt.value) return '尚未刷新';
  const elapsedSeconds = Math.max(0, Math.floor((currentTime.value - capturedAt.value.getTime()) / 1000));
  if (elapsedSeconds === 0) return '刚刚';
  if (elapsedSeconds < 60) return `${elapsedSeconds} 秒前`;
  if (elapsedSeconds >= 3600) return '1 小时前';
  return `${Math.floor(elapsedSeconds / 60)} 分钟前`;
});
const imageViewerStyle = computed(() => ({
  transform: `translate(${imageViewerOffsetX.value}px, ${imageViewerOffsetY.value}px) scale(${imageViewerScale.value})`,
}));

function clearKeyboardFeedback() {
  keyboardError.value = undefined;
  keyboardStatus.value = undefined;
}

async function captureScreenshot() {
  if (!isPageActive || isCapturing.value) return;
  clearAutomaticCapture();
  const controller = new AbortController();
  screenshotController = controller;
  isCapturing.value = true;
  screenshotError.value = undefined;
  try {
    const response = await apiFetch('/api/desktop/screenshot', {
      cache: 'no-store',
      signal: controller.signal,
    });
    if (!response.ok) throw new Error('桌面刷新失败');
    const imageBlob = await response.blob();
    if (!isPageActive || screenshotController !== controller) return;
    const nextImageUrl = URL.createObjectURL(imageBlob);
    if (imageUrl.value) URL.revokeObjectURL(imageUrl.value);
    imageUrl.value = nextImageUrl;
    capturedAt.value = new Date();
    configureRelativeTime();
    lastCaptureFailureAt = undefined;
  } catch (error) {
    if (isPageActive && screenshotController === controller && !isAbortError(error)) {
      screenshotError.value = userErrorMessage(error, '桌面刷新失败');
      lastCaptureFailureAt = Date.now();
    }
  } finally {
    if (screenshotController === controller) {
      screenshotController = undefined;
      if (isPageActive) {
        isCapturing.value = false;
        configureAutomaticCapture();
      }
    }
  }
}

function clearAutomaticCapture() {
  if (autoCaptureTimer !== undefined) {
    window.clearTimeout(autoCaptureTimer);
    autoCaptureTimer = undefined;
  }
}

function configureAutomaticCapture() {
  clearAutomaticCapture();
  if (!isPageActive || document.visibilityState !== 'visible' || isCapturing.value || activeIntervalMilliseconds.value <= 0) return;
  const lastCaptureAt = lastCaptureFailureAt ?? capturedAt.value?.getTime();
  const delay =
    lastCaptureAt === undefined
      ? 0
      : Math.max(0, lastCaptureAt + activeIntervalMilliseconds.value - Date.now());
  autoCaptureTimer = window.setTimeout(() => {
    autoCaptureTimer = undefined;
    void captureScreenshot();
  }, delay);
}

function updateSelectedKeys(keys: string[]) {
  if (safeModeEnabled.value || isSendingKeys.value) return;
  clearKeyboardFeedback();
  selectedKeys.value = keys;
}

async function sendSelectedKeys() {
  if (safeModeEnabled.value) return;
  if (!keyboardEnabled.value || isSendingKeys.value) return;
  if (selectedKeys.value.length === 0) {
    clearKeyboardFeedback();
    keyboardError.value = '请选择至少一个按键';
    return;
  }
  const controller = new AbortController();
  keyboardController = controller;
  isSendingKeys.value = true;
  clearKeyboardFeedback();
  try {
    const response = await apiFetch('/api/desktop/keyboard', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      signal: controller.signal,
      body: JSON.stringify({ keys: selectedKeys.value }),
    });
    if (!response.ok) throw new Error('键盘输入发送失败');
    if (keyboardController !== controller || !keyboardEnabled.value) return;
    selectedKeys.value = [];
    keyboardStatus.value = '按键已发送';
    showToast('键盘输入已发送', 'success');
  } catch (error) {
    if (keyboardController === controller && !isAbortError(error)) {
      const message = userErrorMessage(error, '键盘输入发送失败');
      keyboardError.value = message;
      showToast(message);
    }
  } finally {
    if (keyboardController === controller) {
      keyboardController = undefined;
      isSendingKeys.value = false;
    }
  }
}

function resetKeyboard() {
  keyboardController?.abort();
  keyboardController = undefined;
  selectedKeys.value = [];
  clearKeyboardFeedback();
  isSendingKeys.value = false;
}

function openImageViewer() {
  resetImageViewer();
  releaseImageViewerScroll ??= lockScroll(getPageScroller());
  isImageViewerOpen.value = true;
}

function closeImageViewer() {
  isImageViewerOpen.value = false;
  releaseImageViewerScroll?.();
  releaseImageViewerScroll = undefined;
  resetImageViewer();
}

function resetImageViewer() {
  imageViewerScale.value = 1;
  imageViewerOffsetX.value = 0;
  imageViewerOffsetY.value = 0;
  imageViewerPointers.clear();
  imageViewerPanPointerId = undefined;
  imageViewerPinchDistance = 0;
}

function handleImageViewerWheel(event: WheelEvent) {
  const scaleFactor = event.deltaY < 0 ? 1.15 : 1 / 1.15;
  setImageViewerScale(imageViewerScale.value * scaleFactor);
}

function handleImageViewerPointerDown(event: PointerEvent) {
  event.preventDefault();
  imageViewerPointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
  const target = event.currentTarget as HTMLImageElement;
  target.setPointerCapture(event.pointerId);
  if (imageViewerPointers.size === 2) {
    imageViewerPanPointerId = undefined;
    imageViewerPinchDistance = getImageViewerPointerDistance();
    imageViewerPinchScale = imageViewerScale.value;
    return;
  }
  beginImageViewerPan(event.pointerId, event.clientX, event.clientY);
}

function handleImageViewerPointerMove(event: PointerEvent) {
  if (!imageViewerPointers.has(event.pointerId)) return;
  event.preventDefault();
  imageViewerPointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
  if (imageViewerPointers.size === 2) {
    if (imageViewerPinchDistance > 0)
      setImageViewerScale(
        (imageViewerPinchScale * getImageViewerPointerDistance()) / imageViewerPinchDistance,
      );
    return;
  }
  if (imageViewerPanPointerId !== event.pointerId || imageViewerScale.value <= 1) return;
  setImageViewerOffset(
    imageViewerPanOffsetX + event.clientX - imageViewerPanStartX,
    imageViewerPanOffsetY + event.clientY - imageViewerPanStartY,
  );
}

function handleImageViewerPointerEnd(event: PointerEvent) {
  imageViewerPointers.delete(event.pointerId);
  const target = event.currentTarget as HTMLImageElement;
  if (target.hasPointerCapture(event.pointerId)) target.releasePointerCapture(event.pointerId);
  if (imageViewerPointers.size === 1) {
    const [pointerId, pointer] = imageViewerPointers.entries().next().value as [
      number,
      { x: number; y: number },
    ];
    beginImageViewerPan(pointerId, pointer.x, pointer.y);
    imageViewerPinchDistance = 0;
    return;
  }
  imageViewerPanPointerId = undefined;
  imageViewerPinchDistance = 0;
}

function beginImageViewerPan(pointerId: number, clientX: number, clientY: number) {
  imageViewerPanPointerId = pointerId;
  imageViewerPanStartX = clientX;
  imageViewerPanStartY = clientY;
  imageViewerPanOffsetX = imageViewerOffsetX.value;
  imageViewerPanOffsetY = imageViewerOffsetY.value;
}

function getImageViewerPointerDistance() {
  const [first, second] = [...imageViewerPointers.values()];
  return Math.hypot(second.x - first.x, second.y - first.y);
}

function setImageViewerScale(nextScale: number) {
  imageViewerScale.value = Math.min(6, Math.max(1, nextScale));
  if (imageViewerScale.value === 1) {
    imageViewerOffsetX.value = 0;
    imageViewerOffsetY.value = 0;
    return;
  }
  setImageViewerOffset(imageViewerOffsetX.value, imageViewerOffsetY.value);
}

function setImageViewerOffset(nextX: number, nextY: number) {
  const stage = imageViewer.value;
  const image = imageViewerImage.value;
  if (!stage || !image || imageViewerScale.value <= 1) {
    imageViewerOffsetX.value = 0;
    imageViewerOffsetY.value = 0;
    return;
  }
  const maximumX = Math.max(0, (image.offsetWidth * imageViewerScale.value - stage.clientWidth) / 2);
  const maximumY = Math.max(0, (image.offsetHeight * imageViewerScale.value - stage.clientHeight) / 2);
  imageViewerOffsetX.value = Math.min(maximumX, Math.max(-maximumX, nextX));
  imageViewerOffsetY.value = Math.min(maximumY, Math.max(-maximumY, nextY));
}

function updateRelativeTime() {
  currentTime.value = Date.now();
  if (capturedAt.value && currentTime.value - capturedAt.value.getTime() >= 3600000) clearRelativeTime();
}
function clearRelativeTime() {
  if (relativeTimeTimer === undefined) return;
  window.clearInterval(relativeTimeTimer);
  relativeTimeTimer = undefined;
}
function configureRelativeTime() {
  clearRelativeTime();
  updateRelativeTime();
  if (isPageActive && document.visibilityState === 'visible' && capturedAt.value && currentTime.value - capturedAt.value.getTime() < 3600000)
    relativeTimeTimer = window.setInterval(updateRelativeTime, 1000);
}
function handlePageResume() {
  configureRelativeTime();
  configureAutomaticCapture();
}

watch([autoRefreshEnabled, intervalOption, customIntervalSeconds], configureAutomaticCapture);
watch([keyboardEnabled, safeModeEnabled], ([enabled, restricted]) => {
  if (!enabled || restricted) resetKeyboard();
}, { flush: 'sync' });
watch(isImageViewerOpen, async (open, _previous, onCleanup) => {
  if (!open) return;
  let active = true;
  let releaseFocus: (() => void) | undefined;
  let releaseTouch: (() => void) | undefined;
  onCleanup(() => {
    active = false;
    releaseTouch?.();
    releaseFocus?.();
  });
  await nextTick();
  if (!active || !imageViewer.value) return;
  releaseFocus = manageModalFocus(imageViewer.value);
  releaseTouch = manageModalTouch(imageViewer.value);
});

onMounted(() => {
  isPageActive = true;
  document.addEventListener('visibilitychange', handlePageResume);
  window.addEventListener('focus', handlePageResume);
});

onBeforeUnmount(() => {
  isPageActive = false;
  clearAutomaticCapture();
  clearRelativeTime();
  document.removeEventListener('visibilitychange', handlePageResume);
  window.removeEventListener('focus', handlePageResume);
  screenshotController?.abort();
  screenshotController = undefined;
  if (imageUrl.value) {
    URL.revokeObjectURL(imageUrl.value);
    imageUrl.value = undefined;
  }
  closeImageViewer();
  resetKeyboard();
});
</script>

<template>
  <section class="desktop-viewer" aria-label="桌面">
    <div class="desktop-control-bar">
      <div class="capture-controls">
        <label class="toggle-control">
          <input v-model="autoRefreshEnabled" type="checkbox" />
          <span>自动刷新</span>
        </label>
        <label class="field-control interval-control">
          <span>间隔</span>
          <AppSelect
            v-model="intervalOption"
            :options="[
              { value: '1', label: '1 秒' },
              { value: '3', label: '3 秒' },
              { value: '5', label: '5 秒' },
              { value: '10', label: '10 秒' },
              { value: 'custom', label: '自定义' },
            ]"
          />
        </label>
        <label
          v-if="intervalOption === 'custom'"
          class="field-control interval-control custom-interval-control"
        >
          <span>秒</span>
          <input
            v-model.number="customIntervalSeconds"
            type="number"
            min="1"
            max="600"
            step="1"
            inputmode="numeric"
            :aria-invalid="!isCustomIntervalValid"
          />
        </label>
        <p
          v-if="intervalOption === 'custom' && !isCustomIntervalValid"
          class="interval-validation"
          role="alert"
        >
          请输入 1 至 600 的整数秒数
        </p>
      </div>
      <div class="desktop-actions">
        <span class="capture-time">
          最近刷新：
          <strong>{{ relativeCapturedAt }}</strong>
        </span>
        <button
          class="button button-primary capture-button"
          type="button"
          :disabled="isCapturing"
          @click="captureScreenshot"
        >
          {{ isCapturing ? '正在刷新…' : '刷新' }}
        </button>
      </div>
    </div>
    <div v-if="screenshotError" class="status-message error" role="alert">
      <span>桌面刷新失败，请稍后重试</span>
      <button class="button button-secondary" type="button" @click="captureScreenshot">重新刷新</button>
    </div>
    <p v-else-if="isCapturing && !imageUrl" class="status-message loading">正在刷新桌面…</p>
    <div class="desktop-frame" aria-label="桌面预览">
      <button
        v-if="imageUrl"
        class="desktop-preview-button"
        type="button"
        aria-label="查看桌面大图"
        @click="openImageViewer"
      >
        <img :src="imageUrl" alt="当前桌面预览" />
      </button>
      <p v-else class="empty-state">暂无桌面预览</p>
    </div>
    <section
      class="keyboard-panel"
      :class="{ 'safe-mode-locked': safeModeEnabled }"
      :inert="safeModeEnabled"
      aria-labelledby="keyboard-title"
    >
      <div class="keyboard-panel-heading">
        <div>
          <h2 id="keyboard-title">
            键盘模拟
            <span v-if="safeModeEnabled" class="safe-mode-lock-icon" :title="safeModeMessage">
              <AppIcon name="lock" />
            </span>
          </h2>
          <p>手动发送指定键盘按键，支持组合键</p>
        </div>
        <label class="toggle-control">
          <input v-model="keyboardEnabled" type="checkbox" :disabled="safeModeEnabled" />
          <span>启用键盘模拟</span>
        </label>
      </div>
      <div v-if="keyboardEnabled && !safeModeEnabled">
        <KeyboardKeyPicker
          :model-value="selectedKeys"
          :disabled="isSendingKeys"
          help="可选择普通按键或最多 3 个组合键，确认后点击发送"
          @update:model-value="updateSelectedKeys"
          @limit="keyboardStatus = undefined; keyboardError = '每次最多选择 3 个按键'"
        />
        <p v-if="keyboardError" class="keyboard-message error" role="alert">{{ keyboardError }}</p>
        <p v-else-if="keyboardStatus" class="keyboard-message success" role="status">{{ keyboardStatus }}</p>
        <div class="keyboard-actions">
          <button
            class="button button-secondary"
            type="button"
            :disabled="isSendingKeys || !selectedKeys.length"
            @click="resetKeyboard"
          >
            清空
          </button>
          <button
            class="button button-primary"
            type="button"
            :disabled="isSendingKeys"
            @click="sendSelectedKeys"
          >
            {{ isSendingKeys ? '正在发送…' : '发送' }}
          </button>
        </div>
      </div>
    </section>
    <div
      v-if="isImageViewerOpen && imageUrl"
      ref="imageViewer"
      class="desktop-image-viewer"
      role="dialog"
      tabindex="-1"
      aria-modal="true"
      aria-label="桌面大图"
      @click.self="closeImageViewer"
      @wheel.prevent="handleImageViewerWheel"
      @keydown.esc.stop.prevent="closeImageViewer"
    >
      <button
        class="desktop-image-viewer-close"
        type="button"
        aria-label="关闭大图"
        @click="closeImageViewer"
      >
        ×
      </button>
      <img
        ref="imageViewerImage"
        :src="imageUrl"
        :style="imageViewerStyle"
        alt="当前桌面预览大图"
        @dblclick.prevent="resetImageViewer"
        @pointerdown="handleImageViewerPointerDown"
        @pointermove="handleImageViewerPointerMove"
        @pointerup="handleImageViewerPointerEnd"
        @pointercancel="handleImageViewerPointerEnd"
      />
    </div>
  </section>
</template>
