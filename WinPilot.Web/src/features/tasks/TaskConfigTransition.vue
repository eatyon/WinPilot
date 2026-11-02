<script setup lang="ts">
import { usePanelEffects } from '../../ui/panelEffects';

defineProps<{ value: string }>();
const { interactionAnimationEnabled } = usePanelEffects();
</script>

<template>
  <Transition
    name="task-action"
    :css="interactionAnimationEnabled"
    :mode="interactionAnimationEnabled ? 'out-in' : undefined"
    @before-leave="(element) => element.setAttribute('inert', '')"
    @leave-cancelled="(element) => element.removeAttribute('inert')"
  >
    <div :key="value" class="task-config-transition task-action-transition">
      <div class="task-config-transition-content task-action-content"><slot /></div>
    </div>
  </Transition>
</template>

<style scoped>
.task-config-transition-content { display: flow-root; }
</style>
