<script setup lang="ts">
import { RouterLink } from 'vue-router';
</script>
<template>
  <section class="plugin-market-view">
    <section class="plugins-panel">
      <header class="plugins-heading plugin-market-heading">
        <div>
          <h2>插件市场</h2>
          <p>浏览和发现可安装的插件</p>
        </div>
        <RouterLink class="button button-secondary" to="/plugins">插件管理</RouterLink>
      </header>
      <div class="plugin-list-summary">
        <div class="plugin-summary-counts">
          <span>
            共
            <strong>0</strong>
            个插件
          </span>
        </div>
        <div class="plugin-search">
          <input
                        class="text-input"
            type="search"
            aria-label="搜索插件名称、ID 或作者"
            placeholder="搜索名称、ID 或作者"
            disabled
          />
        </div>
      </div>
    </section>
    <div class="plugin-market-empty">
      <strong>插件市场即将开放</strong>
      <p>云端插件下载、安装和更新功能正在准备中</p>
    </div>
  </section>
</template>
