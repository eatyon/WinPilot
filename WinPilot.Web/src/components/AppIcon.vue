<script setup lang="ts">
import {
  ArrowRight, Bot, CalendarDays, ChevronDown, ChevronRight, ChevronUp, CircleAlert, CircleCheck, Clock,
  Copy, Ellipsis, Eye, EyeOff, FileText, Folder, House, Info, LayoutGrid, List, Lock,
  Menu, Monitor, Palette, PanelLeftClose, PanelLeftOpen, Plus, Puzzle, RotateCw,
  Server, Settings, ShieldCheck, Terminal, UserRound, X,
} from '@lucide/vue';
import type { Component } from 'vue';
import { NIcon } from 'naive-ui';

defineProps<{ name: string }>();

const icons: Record<string, Component> = {
  menu: Menu,
  'menu-expand': PanelLeftOpen,
  'menu-collapse': PanelLeftClose,
  palette: Palette,
  account: UserRound,
  'alert-circle': CircleAlert,
  close: X,
  'rotate-cw': RotateCw,
  list: List,
  bot: Bot,
  home: House,
  desktop: Monitor,
  apps: LayoutGrid,
  clock: Clock,
  schedule: CalendarDays,
  terminal: Terminal,
  files: Folder,
  logs: FileText,
  plugins: Puzzle,
  settings: Settings,
  more: Ellipsis,
  arrow: ArrowRight,
  server: Server,
  shield: ShieldCheck,
  eye: Eye,
  'eye-off': EyeOff,
  info: Info,
  copy: Copy,
  lock: Lock,
  'check-circle': CircleCheck,
  plus: Plus,
  'chevron-down': ChevronDown,
  'chevron-right': ChevronRight,
  'chevron-up': ChevronUp,
};
</script>

<template>
  <NIcon v-if="icons[name]" class="app-icon" aria-hidden="true">
    <component :is="icons[name]" :stroke-width="1.8" focusable="false" />
  </NIcon>
</template>

<style>
.app-icon { width: 20px; height: 20px; flex: 0 0 auto; }
.n-button .app-icon { width: 18px; height: 18px; }
.n-icon.app-icon > svg { display: block; width: 100%; height: 100%; }
</style>
