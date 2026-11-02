<script setup lang="ts">
import { NCard, NList, NListItem, NThing } from 'naive-ui';
import AppModal from '../../components/AppModal.vue';
import type { CommandGroup } from './commandBotModels';

defineProps<{ prefix: string; groups: readonly CommandGroup[] }>();
const emit = defineEmits<{ close: [] }>();
</script>

<template>
  <AppModal title="可用命令" body-max-height="65dvh" @close="emit('close')">
    <div class="available-commands load-error-layout">
        <NCard v-for="group in groups" :key="group.name" :title="group.name" embedded size="small" :segmented="{ content: true }">
          <NList :show-divider="true" :theme-overrides="{ color: 'transparent', colorModal: 'transparent' }">
            <NListItem v-for="command in group.commands" :key="command.usage">
              <NThing :title="prefix + command.usage" :description="command.description" />
            </NListItem>
          </NList>
        </NCard>
    </div>
  </AppModal>
</template>

<style scoped>
.available-commands {
  --load-error-layout-gap: 20px;
  display: grid;
  gap: 20px;
  overflow-wrap: anywhere;
}
</style>
