export type RefreshInterval = '1' | '3' | '5' | '10' | 'custom';

export function isCustomRefreshIntervalValid(seconds: number): boolean {
  return Number.isInteger(seconds) && seconds >= 1 && seconds <= 600;
}

export function refreshIntervalMilliseconds(
  enabled: boolean,
  interval: RefreshInterval,
  customSeconds: number,
): number {
  if (!enabled) return 0;
  if (interval === 'custom' && !isCustomRefreshIntervalValid(customSeconds)) return 0;
  return (interval === 'custom' ? customSeconds : Number(interval)) * 1000;
}

export function formatRefreshTime(capturedAt: Date | undefined, now: number): string {
  if (!capturedAt) return '尚未刷新';
  const elapsedSeconds = Math.max(0, Math.floor((now - capturedAt.getTime()) / 1000));
  if (elapsedSeconds === 0) return '刚刚';
  if (elapsedSeconds < 60) return `${elapsedSeconds} 秒前`;
  if (elapsedSeconds >= 3600) return '1 小时前';
  return `${Math.floor(elapsedSeconds / 60)} 分钟前`;
}
