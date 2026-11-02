import assert from 'node:assert/strict';
import test from 'node:test';
import { bindImagePreviewTouchDrag } from '../../src/features/desktop/imagePreviewTouch.ts';

function fixture() {
  const handlers = new Map(), viewHandlers = new Map(), mouse = [];
  const view = {
    MouseEvent: class { constructor(type, init) { this.type = type; Object.assign(this, init); } },
    addEventListener: (type, handler) => viewHandlers.set(type, handler),
    removeEventListener: type => viewHandlers.delete(type),
  };
  const document = {
    defaultView: view, visibilityState: 'visible',
    addEventListener: (type, handler) => handlers.set(type, handler),
    removeEventListener: type => handlers.delete(type),
    dispatchEvent: event => mouse.push(event),
  };
  const image = { dispatchEvent: event => mouse.push(event) };
  const release = bindImagePreviewTouchDrag({ ownerDocument: document, querySelector: () => image });
  const touch = (identifier = 1, clientX = 10, clientY = 20) => ({ identifier, clientX, clientY });
  function send(type, touches = [], changedTouches = touches, target = image) {
    const event = { target, touches, changedTouches, cancelable: true, prevented: false, preventDefault() { this.prevented = true; } };
    handlers.get(type)?.(event);
    return event;
  }
  return { handlers, viewHandlers, document, mouse, touch, send, release };
}

test('single-finger input delegates coordinates to library mouse dragging and suppresses compatibility mouse events', () => {
  const f = fixture();
  try {
    assert.equal(f.send('touchstart', [f.touch()]).prevented, false);
    assert.equal(f.send('touchmove', [f.touch(1, 80, 90)]).prevented, true);
    f.send('touchend', [], [f.touch(1, 85, 95)]);
    assert.deepEqual(f.mouse.map(e => [e.type, e.clientX, e.clientY, e.buttons]), [
      ['mousedown', 10, 20, 1], ['mousemove', 80, 90, 1], ['mouseup', 85, 95, 0],
    ]);
  } finally { f.release(); }
});

test('toolbar touches are ignored and adding a second finger ends dragging without implementing pinch zoom', () => {
  const f = fixture();
  try {
    assert.equal(f.send('touchstart', [f.touch()], [], {}).prevented, false);
    assert.equal(f.mouse.length, 0);
    f.send('touchstart', [f.touch()]);
    f.send('touchmove', [f.touch(1, 80, 90)]);
    f.send('touchstart', [f.touch(), f.touch(2)]);
    f.send('touchmove', [f.touch(2, 100, 100)]);
    assert.deepEqual(f.mouse.map(e => e.type), ['mousedown', 'mousemove', 'mouseup']);
  } finally { f.release(); }
});

test('cancellation, backgrounding, blur and disposal end library dragging and release all listeners', () => {
  for (const reason of ['touchcancel', 'visibilitychange', 'blur', 'dispose']) {
    const f = fixture();
    f.send('touchstart', [f.touch()]);
    f.send('touchmove', [f.touch(1, 80, 90)]);
    if (reason === 'dispose') f.release();
    else if (reason === 'blur') f.viewHandlers.get('blur')();
    else { f.document.visibilityState = 'hidden'; f.send(reason); }
    f.release();
    f.release();
    assert.deepEqual(f.mouse.map(e => e.type), ['mousedown', 'mousemove', 'mouseup'], reason);
    assert.equal(f.handlers.size, 0);
    assert.equal(f.viewHandlers.size, 0);
  }
});

test('two taps with small finger movement do not suppress native double-click generation', () => {
  const f = fixture();
  try {
    for (let tap = 0; tap < 2; tap++) {
      assert.equal(f.send('touchstart', [f.touch()]).prevented, false);
      assert.equal(f.send('touchmove', [f.touch(1, 12, 22)]).prevented, false);
      assert.equal(f.send('touchend', [], [f.touch(1, 12, 22)]).prevented, false);
    }
    assert.deepEqual(f.mouse, []);
  } finally { f.release(); }
});
