import assert from 'node:assert/strict';
import test from 'node:test';
import { getPageScroller, lockScroll } from '../../src/ui/scrollLock.ts';

function fixture() {
  const document = {};
  const element = () => ({ ownerDocument: document, style: { overflow: '', overscrollBehavior: '' }, scrollTop: 320 });
  document.documentElement = element();
  document.body = element();
  document.scrollingElement = document.documentElement;
  return { document, element };
}

test('page scrolling uses the document root with a documentElement fallback', () => {
  const previous = globalThis.document;
  const { document } = fixture();
  globalThis.document = document;
  try {
    assert.equal(getPageScroller(), document.documentElement);
    document.scrollingElement = null;
    assert.equal(getPageScroller(), document.documentElement);
  } finally { globalThis.document = previous; }
});

test('nested root locks preserve scroll position without changing body overflow', () => {
  const { document } = fixture();
  document.body.style.overflow = 'clip';
  const first = lockScroll(document.documentElement);
  const second = lockScroll(document.documentElement);
  first();
  first();
  assert.equal(document.documentElement.style.overflow, 'hidden');
  assert.equal(document.body.style.overflow, 'clip');
  assert.equal(document.documentElement.scrollTop, 320);
  second();
  assert.equal(document.documentElement.style.overflow, '');
  assert.equal(document.body.style.overflow, 'clip');
  assert.equal(document.documentElement.style.overscrollBehavior, '');
});

test('locking an internal control does not lock the document', () => {
  const { document, element } = fixture();
  const control = element();
  const release = lockScroll(control);
  assert.equal(control.style.overflow, 'hidden');
  assert.equal(document.documentElement.style.overflow, '');
  release();
  assert.equal(control.style.overflow, '');
});

test('root locks replace the reserved gutter with padding only once and restore it', () => {
  const { document } = fixture();
  const root = document.documentElement;
  root.style.scrollbarGutter = 'stable';
  root.style.paddingRight = '4px';
  root.getBoundingClientRect = () => ({ width: 1265 });
  document.defaultView = { innerWidth: 1280, getComputedStyle: () => ({ paddingRight: '4px' }) };
  const first = lockScroll(root);
  const second = lockScroll(root);
  assert.equal(root.style.scrollbarGutter, 'auto');
  assert.equal(root.style.paddingRight, '19px');
  first();
  first();
  assert.equal(root.style.paddingRight, '19px');
  second();
  assert.equal(root.style.scrollbarGutter, 'stable');
  assert.equal(root.style.paddingRight, '4px');
});

test('overlay scrollbars do not add a gutter compensation', () => {
  const { document } = fixture();
  const root = document.documentElement;
  root.style.scrollbarGutter = '';
  root.style.paddingRight = '';
  root.getBoundingClientRect = () => ({ width: 390 });
  document.defaultView = { innerWidth: 390, getComputedStyle: () => ({ paddingRight: '0px' }) };
  const release = lockScroll(root);
  assert.equal(root.style.paddingRight, '0px');
  release();
  assert.equal(root.style.paddingRight, '');
  assert.equal(root.style.scrollbarGutter, '');
});
