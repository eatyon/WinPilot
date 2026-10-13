import { createApp } from 'vue'
import './style.css'
import App from './App.vue'
import { router } from './router'
import { initializeTheme } from './ui/theme'

initializeTheme()
const app = createApp(App).use(router)
const mount = () => { app.mount('#app') }
void router.isReady().then(mount, mount)
