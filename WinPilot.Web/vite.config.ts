import vue from '@vitejs/plugin-vue';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [vue()],
  build: {
    outDir: '../WinPilot/wwwroot',
    emptyOutDir: true,
    rolldownOptions: {
      output: {
        codeSplitting: {
          groups: [
            { name: 'vue', tags: ['$initial'], test: /\/node_modules\/(?:@vue\/|vue\/|vue-router\/)/ },
            { name: 'ui-utils', tags: ['$initial'], test: /\/node_modules\/(?:lodash-es|css-render|@css-render\/[^/]+|seemly|evtd|vooks|vdirs)\// },
          ],
        },
      },
    },
  },
});
