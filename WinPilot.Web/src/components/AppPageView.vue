<script setup lang="ts">
import { RouterView } from 'vue-router';
import { usePanelEffects } from '../ui/panelEffects';
import { getPageScroller } from '../ui/scrollLock';

const { pageTransitionEnabled, pageTransitionEffect } = usePanelEffects();
function resetPageScroll() {
  getPageScroller().scrollTop = 0;
}
</script>

<template>
  <RouterView v-slot="{ Component, route }">
    <Transition
      :css="pageTransitionEnabled"
      :name="`page-${pageTransitionEffect}`"
      :mode="pageTransitionEnabled ? 'out-in' : undefined"
      appear
      @before-enter="resetPageScroll"
    >
      <div v-if="Component" :key="route.path" class="route-page" data-page-transition>
        <component :is="Component" />
      </div>
    </Transition>
  </RouterView>
</template>
