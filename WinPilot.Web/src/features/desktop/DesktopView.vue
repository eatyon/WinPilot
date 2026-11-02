<script setup lang="ts">
import AppLoadError from '../../components/AppLoadError.vue';
import AppLoading from '../../components/AppLoading.vue';
import AppTooltip from '../../components/AppTooltip.vue';
import { cloneVNode, computed, h, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { apiFetch, isAbortError, userErrorMessage } from '../../api/client';
import KeyboardKeyPicker from '../../components/KeyboardKeyPicker.vue';
import { orderKeys } from '../../ui/keyboardLayout';
import { NEmpty, NButton, NCard, NCheckbox, NCollapseTransition, NImagePreview, type ImageRenderToolbarProps } from 'naive-ui';
import RefreshToolbar from '../../components/RefreshToolbar.vue';
import { safeModeEnabled } from '../security/safeMode';
import { showToast } from '../../ui/toast';
import { getPageScroller, lockScroll } from '../../ui/scrollLock';
import { manageModalFocus } from '../../ui/modalFocus';
import { manageModalTouch } from '../../ui/modalTouch';
import { bindImagePreviewTouchDrag } from './imagePreviewTouch';
import {
  formatRefreshTime,
  isCustomRefreshIntervalValid,
  refreshIntervalMilliseconds,
  type RefreshInterval,
} from '../../ui/refreshTiming';

const imageUrl = ref<string>();
const isImageViewerOpen = ref(false);
const imageViewerToolbar = ref<HTMLElement>();
const capturedAt = ref<Date>();
const screenshotError = ref<string>();
const isCapturing = ref(false);
const autoRefreshEnabled = ref(false);
const intervalOption = ref<RefreshInterval>('5');
const customIntervalSeconds = ref(15);
const keyboardEnabled = ref(false);
const selectedKeys = ref<string[]>([]);
const isSendingKeys = ref(false);
let autoCaptureTimer: number | undefined;
let relativeTimeTimer: number | undefined;
let lastCaptureFailureAt: number | undefined;
let screenshotController: AbortController | undefined;
let keyboardController: AbortController | undefined;
let isPageActive = false;
let releaseImageViewerScroll: (() => void) | undefined;
const currentTime = ref(Date.now());
const isCustomIntervalValid = computed(
  () => isCustomRefreshIntervalValid(customIntervalSeconds.value),
);
const activeIntervalMilliseconds = computed(() =>
  refreshIntervalMilliseconds(autoRefreshEnabled.value, intervalOption.value, customIntervalSeconds.value),
);
const relativeCapturedAt = computed(() => formatRefreshTime(capturedAt.value, currentTime.value));

async function captureScreenshot() {
  if (!isPageActive || isCapturing.value) return;
  clearAutomaticCapture();
  const controller = new AbortController();
  screenshotController = controller;
  isCapturing.value = true;
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
    screenshotError.value = undefined;
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

// 页面可见且没有采集进行时才安排下一次刷新；以上次成功或失败时间控制重试间隔。
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
  selectedKeys.value = orderKeys(keys);
}

async function sendSelectedKeys() {
  if (safeModeEnabled.value) return;
  if (!keyboardEnabled.value || isSendingKeys.value) return;
  if (selectedKeys.value.length === 0) {
    showToast('请选择至少一个按键');
    return;
  }
  const controller = new AbortController();
  keyboardController = controller;
  isSendingKeys.value = true;
  try {
    const response = await apiFetch('/api/desktop/keyboard', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      signal: controller.signal,
      body: JSON.stringify({ keys: orderKeys(selectedKeys.value) }),
    });
    if (!response.ok) throw new Error('键盘输入发送失败');
    if (keyboardController !== controller || !keyboardEnabled.value) return;
    selectedKeys.value = [];
    showToast('键盘输入已发送', 'success');
  } catch (error) {
    if (keyboardController === controller && !isAbortError(error)) {
      const message = userErrorMessage(error, '键盘输入发送失败');
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
  isSendingKeys.value = false;
}

function openImageViewer() {
  releaseImageViewerScroll ??= lockScroll(getPageScroller());
  isImageViewerOpen.value = true;
}

function closeImageViewer() {
  isImageViewerOpen.value = false;
  releaseImageViewerScroll?.();
  releaseImageViewerScroll = undefined;
}

function renderImageToolbar({ nodes }: ImageRenderToolbarProps) {
  const actions = [
    [nodes.rotateClockwise, '顺时针旋转90度'],
    [nodes.zoomOut, '缩小'],
    [nodes.zoomIn, '放大'],
    [nodes.close, '关闭大图'],
  ] as const;
  return h('div', {
    class: 'image-preview-actions',
    ref: (element: unknown) => {
      imageViewerToolbar.value = element instanceof HTMLElement ? element : undefined;
    },
  }, actions.map(([node, label]) => {
    const icon = cloneVNode(node);
    icon.props = { ...icon.props, onClick: undefined, 'aria-hidden': true };
    return h(AppTooltip, { text: label }, {
      default: () => h(NButton, {
        quaternary: true,
        class: 'app-icon-button',
        color: 'var(--n-toolbar-icon-color, var(--text))',
        'aria-label': label,
        onClick: node.props?.onClick,
      }, { icon: () => icon }),
    });
  }));
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
watch([isImageViewerOpen, imageViewerToolbar], async ([open, toolbar], _previous, onCleanup) => {
  if (!open || !toolbar) return;
  let active = true;
  let releaseFocus: (() => void) | undefined;
  let releaseTouch: (() => void) | undefined;
  let releaseDrag: (() => void) | undefined;
  onCleanup(() => {
    active = false;
    releaseDrag?.();
    releaseTouch?.();
    releaseFocus?.();
  });
  await nextTick();
  if (!active || imageViewerToolbar.value !== toolbar) return;
  const container = toolbar.closest<HTMLElement>('.n-image-preview-container');
  if (!container) return;
  container.setAttribute('role', 'dialog');
  container.setAttribute('aria-modal', 'true');
  container.setAttribute('aria-label', '桌面大图');
  container.setAttribute('tabindex', '-1');
  container.querySelector('img')?.setAttribute('alt', '当前桌面预览大图');
  releaseFocus = manageModalFocus(container);
  releaseTouch = manageModalTouch(container);
  releaseDrag = bindImagePreviewTouchDrag(container);
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
  <section class="desktop-viewer load-error-layout" aria-label="桌面">
    <AppLoadError :show="Boolean(screenshotError)" :retry="captureScreenshot" :loading="isCapturing">{{ screenshotError }}</AppLoadError>
    <NCard>
      <RefreshToolbar
        v-model:enabled="autoRefreshEnabled"
        v-model:interval="intervalOption"
        v-model:custom-seconds="customIntervalSeconds"
        :valid="isCustomIntervalValid"
        :refreshing="isCapturing"
        :captured-at="relativeCapturedAt"
        @refresh="captureScreenshot"
      />
    </NCard>
    <NCard
      class="desktop-frame"
      content-class="desktop-frame-content"
      :content-style="{ padding: 0 }"
      :theme-overrides="{ color: 'var(--surface-subtle)', borderColor: 'var(--border-strong)' }"
      aria-label="桌面预览"
    >
      <button
        v-if="imageUrl"
        class="desktop-preview-button"
        type="button"
        aria-label="查看桌面大图"
        @click="openImageViewer"
      >
        <img :src="imageUrl" alt="当前桌面预览" />
      </button>
      <AppLoading v-if="isCapturing && !imageUrl" class="desktop-loading" label="正在刷新桌面" />
      <NEmpty :show-icon="false" v-else-if="!imageUrl" class="empty-state">{{ screenshotError ? '无法加载桌面预览' : '暂无桌面预览' }}</NEmpty>
    </NCard>
    <NCard
      tag="section"
      class="keyboard-card"
      :class="{ 'safe-mode-locked': safeModeEnabled }"
      :inert="safeModeEnabled"
      aria-labelledby="keyboard-title"
    >
      <div class="keyboard-panel-heading">
        <div>
          <h2 id="keyboard-title">
            按键模拟
          </h2>
          <p>手动发送指定键盘按键，支持组合键</p>
        </div>
        <NCheckbox v-model:checked="keyboardEnabled" :disabled="safeModeEnabled">启用按键模拟</NCheckbox>
      </div>
      <NCollapseTransition :show="keyboardEnabled && !safeModeEnabled" :inert="!keyboardEnabled || safeModeEnabled || undefined">
        <div class="keyboard-panel-controls">
          <KeyboardKeyPicker
            :model-value="selectedKeys"
            :disabled="isSendingKeys"
            help="可选择单个按键或组合最多 3 个按键，确认后点击发送"
            @update:model-value="updateSelectedKeys"
            @limit="showToast('每次最多选择 3 个按键')"
          />
          <div class="keyboard-actions">
            <NButton
              :disabled="isSendingKeys || !selectedKeys.length"
              @click="resetKeyboard"
            >
              清空
            </NButton>
            <NButton
              type="primary"
              :loading="isSendingKeys"
              :disabled="isSendingKeys || !selectedKeys.length"
              @click="sendSelectedKeys"
            >
              {{ isSendingKeys ? '正在发送' : '发送' }}
            </NButton>
          </div>
        </div>
      </NCollapseTransition>
    </NCard>
    <NImagePreview
      v-if="imageUrl"
      :src="imageUrl"
      :show="isImageViewerOpen"
      :render-toolbar="renderImageToolbar"
      @close="closeImageViewer"
    />
  </section>
</template>

<style>
.desktop-viewer { display: grid; gap: var(--page-card-row-gap) 18px; }
.desktop-frame { min-height: min(60vw, 680px); overflow: hidden; }
.desktop-frame:has(img) { min-height: 0; }
.desktop-frame img { display: block; width: 100%; max-width: 100%; height: auto; min-width: 0; object-fit: contain; }
.desktop-preview-button { display: block; width: 100%; padding: 0; border: 0; background: transparent; cursor: zoom-in; }
.keyboard-panel-heading { display: flex; align-items: center; justify-content: space-between; gap: 18px; }
.keyboard-panel-heading h2 { margin: 0; font-size: 18px; }
.keyboard-panel-heading p { margin: 7px 0 0; color: var(--text-muted); font-size: 13px; line-height: 1.55; transition: color .3s cubic-bezier(.4, 0, .2, 1); }
.keyboard-panel-controls { padding-top: 25px; }
.keyboard-actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 8px; }
@media (max-width: 767px) {
  .desktop-frame { min-height: 180px; }
  .keyboard-panel-heading { align-items: stretch; flex-direction: column; }
  .keyboard-actions { display: grid; grid-template-columns: 1fr 1fr; }
}
.desktop-frame-content { display: grid; min-height: 0; place-items: center; }
.desktop-frame:not(:has(img)) { background: color-mix(in srgb, var(--canvas) 90%, var(--surface) 10%); }
@media (min-width: 768px) {
  .desktop-frame:not(:has(img)) { min-height: 0; aspect-ratio: 16 / 9; }
}
.desktop-loading { position: absolute; inset: 0; display: grid; place-items: center; margin: 0; }

.n-image-preview-toolbar .n-base-icon { box-sizing: content-box; }
.image-preview-actions { display: flex; align-items: center; gap: 4px; }
.n-image-preview-toolbar .n-button .n-base-icon { padding: 0; font-size: inherit; }
</style>
