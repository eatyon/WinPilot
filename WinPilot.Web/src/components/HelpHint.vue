<script setup lang="ts">
withDefaults(
  defineProps<{
    label: string;
    href?: string;
  }>(),
  { href: undefined },
);
</script>

<template>
  <a
    v-if="href"
    class="help-hint"
    :href="href"
    target="_blank"
    rel="noopener noreferrer"
    :aria-label="label"
    :title="label"
  >
    ?
  </a>
  <button v-else class="help-hint" type="button" :aria-label="label" :title="label">
    ?
  </button>
</template>
