import { createApp } from 'vue';
import './style.css';
import 'vfonts/Lato.css';
import 'vfonts/FiraCode.css';
import App from './App.vue';
import { router } from './router';
import { initializeTheme } from './ui/theme';
import { initializePanelEffects } from './ui/panelEffects';

initializeTheme();
initializePanelEffects();
const app = createApp(App).use(router);
app.mount('#app');
