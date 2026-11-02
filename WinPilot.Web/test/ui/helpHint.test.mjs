import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../../src/components/HelpHint.vue', import.meta.url), 'utf8');

test('help popovers delegate activation and viewport placement to library components', () => {
  assert.match(source, /<NPopover v-else trigger="click"/);
  assert.match(source, /<NButton size="tiny" circle quaternary aria-label="查看说明"/);
  assert.match(source, /import \{ textPopoverStyle \} from '..\/ui\/textPopoverStyle'/);
  assert.match(source, /<NPopover v-else trigger="click" :style="textPopoverStyle"/);
  assert.doesNotMatch(source, /getBoundingClientRect|position:\s*fixed/);
});

test('narrow screens reuse the shared modal and release the viewport listener', () => {
  assert.match(source, /matchMedia\('\(max-width: 767px\)'\)/);
  assert.match(source, /<NButton v-else-if="isNarrow"[^>]*aria-label="查看说明" @click="helpOpen = true"/);
  assert.match(source, /<AppModal v-if="helpOpen" title="说明" size="small" @close="helpOpen = false">/);
  assert.match(source, /white-space: pre-wrap; overflow-wrap: anywhere;/);
  assert.match(source, /onScopeDispose\(\(\) => narrowMedia.removeEventListener\('change', updateWidth\)\)/);
  assert.doesNotMatch(source, /50vw/);
});

test('linked help retains an accessible label and safe external navigation', () => {
  assert.match(source, /<NButton v-if="href" tag="a" :href="href"/);
  assert.match(source, /target="_blank" rel="noopener noreferrer"/);
  assert.match(source, /:aria-label="label"/);
});
