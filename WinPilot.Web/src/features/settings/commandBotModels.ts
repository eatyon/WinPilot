export type CommandBotChannel = 'telegram' | 'feishu' | 'qqbot' | 'onebot';

export interface CommandBotConfiguration {
  enabled: boolean;
  prefix?: string;
  webSocketUrl?: string;
  token?: string;
}

export interface CommandGroup {
  name: string;
  commands: Array<{ usage: string; description: string }>;
}
