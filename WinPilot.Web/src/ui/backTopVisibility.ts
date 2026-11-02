export function createBackTopVisibility() {
  let previous = 0;
  let distance = 0;
  let visible = false;
  return {
    reset() { previous = 0; distance = 0; visible = false; },
    update(top: number) {
      const current = Math.max(0, top);
      const delta = current - previous;
      previous = current;
      if (current < 180) {
        distance = 0;
        return visible = false;
      }
      if (delta !== 0) {
        distance = Math.sign(delta) === Math.sign(distance) ? distance + delta : delta;
        if (Math.abs(distance) >= 16) visible = distance < 0;
      }
      return visible;
    },
  };
}
