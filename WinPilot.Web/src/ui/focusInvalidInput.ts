export function focusInvalidInput(input: HTMLElement | null | undefined, feedbackTarget?: HTMLElement | null) {
  if (!input) return;
  const target = feedbackTarget ?? input;
  target.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  input.focus({ preventScroll: true });
  if (!window.matchMedia('(prefers-reduced-motion: reduce)').matches)
    target.animate([{ transform: 'translateX(0)' }, { transform: 'translateX(-3px)' }, { transform: 'translateX(3px)' }, { transform: 'translateX(-2px)' }, { transform: 'translateX(0)' }], { duration: 220 });
}
