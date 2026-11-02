<script setup lang="ts">
import { NButton } from 'naive-ui';
import { RouterLink, type RouteLocationRaw } from 'vue-router';

defineOptions({ inheritAttrs: false });
defineProps<{ to: RouteLocationRaw }>();
</script>

<template>
  <RouterLink :to="to" custom v-slot="{ href, navigate }">
    <NButton v-bind="$attrs" tag="a" :href="href" @click="navigate">
      <slot />
    </NButton>
  </RouterLink>
</template>
