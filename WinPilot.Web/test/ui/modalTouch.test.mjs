import assert from 'node:assert/strict';
import test from 'node:test';
import { manageModalTouch } from '../../src/ui/modalTouch.ts';
import { registerModalPortal } from '../../src/ui/modalFocus.ts';

class Element {
  scrollTop = 0;
  scrollLeft = 0;
  scrollHeight = 300;
  clientHeight = 100;
  scrollWidth = 100;
  clientWidth = 100;
  overflowY = 'auto';
  overflowX = 'visible';
  parentElement = null;
  getRootNode() { return {}; }
  matches() { return false; }
}

function fixture() {
  const listeners = new Map();
  const document = {
    defaultView: { HTMLElement: Element, getComputedStyle: element => element },
    addEventListener(type, listener) {
      const set = listeners.get(type) ?? new Set();
      set.add(listener);
      listeners.set(type, set);
    },
    removeEventListener(type, listener) { listeners.get(type)?.delete(listener); },
  };
  const modal = Object.assign(new Element(), { ownerDocument: document, overflowY: 'hidden' });
  function dispatch(type, path, y, count = 1, x = 0) {
    const event = {
      touches: count === 1 ? [{ clientX: x, clientY: y }] : [{}, {}],
      composedPath: () => path,
      cancelable: true,
      defaultPrevented: false,
      preventDefault() { this.defaultPrevented = true; },
    };
    for (const listener of listeners.get(type) ?? []) listener(event);
    return event.defaultPrevented;
  }
  const gesture = (path, y, count = 1, x = 0) => {
    dispatch('touchstart', path, 100, count);
    return dispatch('touchmove', path, y, count, x);
  };
  return { document, modal, listeners, gesture, dispatch };
}

test('modal touch blocks backdrop and scroll edges but permits content scrolling', t => {
  const { modal, gesture, listeners } = fixture();
  const release = manageModalTouch(modal);
  t.after(release);
  const content = new Element();
  assert.equal(gesture([], 80), true);
  assert.equal(gesture([content, modal], 120), true);
  assert.equal(gesture([content, modal], 80), false);
  content.scrollTop = 200;
  assert.equal(gesture([content, modal], 80), true);
  assert.equal(gesture([content, modal], 120), false);
  assert.equal(gesture([], 80, 2), false);
  release();
  release();
  assert.equal([...listeners.values()].every(set => set.size === 0), true);
});

test('nested modals block their parent content and restore it after closing', t => {
  const { modal, document, gesture } = fixture();
  const releaseParent = manageModalTouch(modal);
  let releaseChild;
  t.after(() => { releaseChild?.(); releaseParent(); });
  const child = Object.assign(new Element(), { ownerDocument: document, overflowY: 'hidden' });
  releaseChild = manageModalTouch(child);
  const content = new Element();
  assert.equal(gesture([content, modal], 80), true);
  assert.equal(gesture([content, child], 80), false);
  releaseChild();
  assert.equal(gesture([content, modal], 80), false);
  releaseParent();
});

test('ShadowRoot paths, registered menus and horizontal scroll retain their own scrolling', t => {
  const { modal, gesture } = fixture();
  const release = manageModalTouch(modal);
  let unregister;
  t.after(() => { unregister?.(); release(); });
  const content = new Element();
  assert.equal(gesture([content, {}, modal], 80), false);
  const portal = new Element();
  unregister = registerModalPortal(portal, modal);
  assert.equal(gesture([portal], 80), false);
  unregister();
  assert.equal(gesture([portal], 80), true);
  content.overflowX = 'auto';
  content.scrollWidth = 300;
  assert.equal(gesture([content, modal], 100, 1, -20), false);
  content.scrollLeft = 200;
  assert.equal(gesture([content, modal], 100, 1, -20), true);
  release();
});

test('continuous moves track direction and a new gesture resets the touch position', t => {
  const { modal, dispatch } = fixture();
  const release = manageModalTouch(modal);
  t.after(release);
  const content = new Element();
  const path = [content, modal];
  dispatch('touchstart', path, 100);
  assert.equal(dispatch('touchmove', path, 80), false);
  content.scrollTop = 200;
  assert.equal(dispatch('touchmove', path, 60), true);
  assert.equal(dispatch('touchmove', path, 90), false);
  dispatch('touchstart', path, 20);
  assert.equal(dispatch('touchmove', path, 40), false);
  dispatch('touchmove', path, 40, 2);
  assert.equal(dispatch('touchmove', path, 30), false);
  dispatch('touchstart', path, 40);
  assert.equal(dispatch('touchmove', path, 30), true);
  release();
});

test('horizontal range input gestures are not prevented', t => {
  const { modal, gesture } = fixture();
  const release = manageModalTouch(modal);
  t.after(release);
  const range = Object.assign(new Element(), {
    overflowY: 'visible',
    matches: selector => selector === 'input[type="range"]',
  });
  assert.equal(gesture([range, modal], 100, 1, -20), false);
  assert.equal(gesture([range, modal], 80), true);
  release();
});

test('closing a parent first preserves the child lock and final cleanup', t => {
  const { modal, document, gesture, listeners } = fixture();
  const releaseParent = manageModalTouch(modal);
  let releaseChild;
  t.after(() => { releaseChild?.(); releaseParent(); });
  const child = Object.assign(new Element(), { ownerDocument: document, overflowY: 'hidden' });
  releaseChild = manageModalTouch(child);
  releaseParent();
  const content = new Element();
  assert.equal(gesture([content, child], 80), false);
  assert.equal(gesture([content, modal], 80), true);
  releaseChild();
  assert.equal(gesture([], 80), false);
  assert.equal([...listeners.values()].every(set => set.size === 0), true);
});
