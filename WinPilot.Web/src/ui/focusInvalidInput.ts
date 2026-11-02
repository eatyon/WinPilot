export function focusInvalidInput(input: HTMLElement | null | undefined, feedbackTarget?: HTMLElement | null) {
  if (!input) return;
  const target = feedbackTarget ?? input.closest<HTMLElement>('.n-input-group') ?? input.closest<HTMLElement>('.n-input') ?? input;
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches ||
    (typeof document !== 'undefined' && document.documentElement.classList.contains('interaction-animations-disabled'));
  target.scrollIntoView({ behavior: reducedMotion ? 'instant' : 'smooth', block: 'center', inline: 'nearest' });
  input.focus({ preventScroll: true });
  if (!reducedMotion)
    target.animate([{ transform: 'translateX(0)' }, { transform: 'translateX(-3px)' }, { transform: 'translateX(3px)' }, { transform: 'translateX(-2px)' }, { transform: 'translateX(0)' }], { duration: 220 });
}
