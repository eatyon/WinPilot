import assert from 'node:assert/strict';
import test from 'node:test';
import { manageModalFocus, registerModalPortal } from '../../src/ui/modalFocus.ts';

class Shadow extends EventTarget {
  children = [];
  activeElement = null;
  constructor(host) { super(); this.host = host; }
  append(child) { this.children.push(child); child.root = this; }
}

class Element extends EventTarget {
  children = [];
  attrs = new Set();
  parentElement = null;
  tabIndex = -1;
  isConnected = true;
  disabled = false;
  constructor(tag = 'div') { super(); this.tag = tag; if (['button', 'input'].includes(tag)) this.tabIndex = 0; }
  append(child) { this.children.push(child); child.parentElement = this; }
  hasAttribute(name) { return this.attrs.has(name); }
  matches(selector) {
    if (selector === '[inert]') return this.hasAttribute('inert');
    if (selector === ':disabled') return this.disabled;
    return this.tabIndex >= 0;
  }
  getRootNode() { return this.parentElement?.getRootNode() ?? this.root ?? document; }
  getClientRects() { return this.isConnected ? [{}] : []; }
  focus() {
    let element = this;
    let root = element.getRootNode();
    while (root instanceof Shadow) {
      root.activeElement = element;
      element = root.host;
      root = element.getRootNode();
    }
    document.activeElement = element;
    document.dispatchEvent(new Event('focusin'));
  }
}

globalThis.HTMLElement = Element;
globalThis.ShadowRoot = Shadow;
globalThis.getComputedStyle = () => ({ visibility: 'visible' });
globalThis.document = new EventTarget();
document.activeElement = null;

function pressTab(shiftKey = false) {
  const event = new Event('keydown', { cancelable: true });
  Object.assign(event, { key: 'Tab', shiftKey });
  document.dispatchEvent(event);
  return event;
}

function shadowContent(modal) {
  const host = new Element();
  modal.append(host);
  host.shadowRoot = new Shadow(host);
  const input = new Element('input');
  host.shadowRoot.append(input);
  return input;
}

test('Tab reaches ShadowRoot controls and focus cannot escape a modal', () => {
  const outside = new Element('button');
  outside.focus();
  const modal = new Element();
  const close = new Element('button');
  modal.append(close);
  const input = shadowContent(modal);
  const release = manageModalFocus(modal);
  try {
    close.focus();
    assert.equal(pressTab().defaultPrevented, true);
    assert.equal(input.getRootNode().activeElement, input);
    outside.focus();
    assert.equal(document.activeElement, modal);
  } finally { release(); }
  assert.equal(document.activeElement, outside);
});

test('closing a child restores focus inside its parent ShadowRoot', () => {
  const outside = new Element('button');
  outside.focus();
  const parent = new Element();
  const input = shadowContent(parent);
  const releaseParent = manageModalFocus(parent);
  input.focus();
  const child = new Element();
  const releaseChild = manageModalFocus(child);
  try {
    assert.equal(document.activeElement, child);
    releaseChild();
    assert.equal(input.getRootNode().activeElement, input);
    assert.equal(document.activeElement, input.getRootNode().host);
  } finally { releaseChild(); releaseParent(); }
  assert.equal(document.activeElement, outside);
});

test('closing a parent before its child still restores the original page focus', () => {
  const outside = new Element('button');
  outside.focus();
  const parent = new Element();
  const input = shadowContent(parent);
  const releaseParent = manageModalFocus(parent);
  input.focus();
  const child = new Element();
  const releaseChild = manageModalFocus(child);
  releaseParent();
  parent.isConnected = false;
  releaseChild();
  releaseChild();
  assert.equal(document.activeElement, outside);
});

test('inert parent controls are excluded across a ShadowRoot boundary', () => {
  const parent = new Element();
  const input = shadowContent(parent);
  const release = manageModalFocus(parent);
  try {
    parent.attrs.add('inert');
    const event = pressTab();
    assert.equal(event.defaultPrevented, false);
    assert.notEqual(input.getRootNode().activeElement, input);
  } finally { parent.attrs.delete('inert'); release(); }
});

test('a registered dropdown portal follows its ShadowRoot owner in Tab order', () => {
  const modal = new Element();
  const owner = new Element();
  modal.append(owner);
  owner.shadowRoot = new Shadow(owner);
  const trigger = new Element('button');
  owner.shadowRoot.append(trigger);
  const next = new Element('button');
  modal.append(next);
  const portal = new Element();
  const option = new Element('button');
  portal.append(option);
  const unregister = registerModalPortal(portal, owner);
  const release = manageModalFocus(modal);
  try {
    trigger.focus();
    pressTab();
    assert.equal(document.activeElement, option);
    pressTab();
    assert.equal(document.activeElement, next);
  } finally { unregister(); release(); }
});
