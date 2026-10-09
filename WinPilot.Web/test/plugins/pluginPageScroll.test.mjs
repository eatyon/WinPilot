import assert from 'node:assert/strict';
import test from 'node:test';
import { createPluginPageScroll } from '../../src/features/plugins/page/PluginPageScroll.ts';

test('scroll helpers restore positions and account for the scroller border and offset', () => {
  const element = { isConnected: true, getBoundingClientRect: () => ({ top: 180 }) };
  const root = { contains: target => target === element };
  const ownerDocument = {};
  let requested;
  const scroller = { scrollTop: 200, scrollLeft: 4, clientTop: 2, ownerDocument, getBoundingClientRect: () => ({ top: 20 }), scrollTo: options => { requested = options; } };
  const controller = new AbortController();
  const scroll = createPluginPageScroll(root, controller.signal, () => scroller);
  assert.deepEqual(scroll.getPosition(), { top: 200, left: 4 });
  scroll.scrollTo(element, { offset: 10 });
  assert.deepEqual(requested, { top: 348, behavior: 'instant' });
  scroll.restorePosition({ top: 30, left: 0 }, 'smooth');
  assert.deepEqual(requested, { top: 30, left: 0, behavior: 'smooth' });
  assert.throws(() => scroll.scrollTo({}), /当前插件/);
  assert.throws(() => scroll.restorePosition({ top: NaN, left: 0 }), /无效/);
  controller.abort();
  scroll.restorePosition({ top: 400, left: 0 });
  assert.equal(requested.top, 30);
});

test('document scrolling uses viewport coordinates', () => {
  const element = { isConnected: true, getBoundingClientRect: () => ({ top: 100 }) };
  let requested;
  const ownerDocument = {};
  const scroller = { scrollTop: 300, scrollLeft: 0, ownerDocument, scrollTo: options => { requested = options; } };
  ownerDocument.scrollingElement = scroller;
  const scroll = createPluginPageScroll({ contains: () => true }, new AbortController().signal, () => scroller);
  scroll.scrollTo(element);
  assert.equal(requested.top, 400);
});
