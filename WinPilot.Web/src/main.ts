import { createApp } from 'vue'
import './style.css'
import App from './App.vue'
import { router } from './router'
import { initializeTheme } from './ui/theme'

initializeTheme()
createApp(App).use(router).mount('#app')
