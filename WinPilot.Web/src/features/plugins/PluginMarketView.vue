<script setup lang="ts">
import { NCard, NEmpty, NInput, NText } from 'naive-ui';
import AppLinkButton from '../../components/AppLinkButton.vue';
</script>
<template>
  <section class="plugin-market-view">
    <NCard
      tag="section"
      class="plugins-panel"
      header-class="plugins-panel-heading"
      header-extra-class="plugins-panel-actions"
      content-class="plugin-list-summary"
      :segmented="{ content: true }"
    >
      <template #header>
        <div class="plugins-panel-title">
          <NText tag="h2" :depth="1">插件市场</NText>
          <NText tag="p" :depth="2">浏览和发现可安装的插件</NText>
        </div>
      </template>
      <template #header-extra>
        <AppLinkButton to="/plugins">插件管理</AppLinkButton>
      </template>
      <template #default>
        <div class="plugin-summary-counts">
          <NText :depth="2">
            共
            <NText tag="strong" :depth="1">0</NText>
            个插件
          </NText>
        </div>
        <div class="plugin-search">
          <NInput
            clearable
            aria-label="搜索插件名称、ID 或作者"
            placeholder="搜索名称、ID 或作者"
            disabled
            :input-props="{ role: 'searchbox', enterkeyhint: 'search', 'aria-label': '搜索插件名称、ID 或作者' }"
          />
        </div>
      </template>
    </NCard>
    <NCard>
      <NEmpty description="插件市场暂未开放" />
    </NCard>
  </section>
</template>

<style>
.plugin-market-view { display: grid; width: 100%; max-width: none; gap: var(--page-card-row-gap) 18px; }
</style>
