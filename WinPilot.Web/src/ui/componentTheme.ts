import { darkTheme, lightTheme, type GlobalThemeOverrides } from 'naive-ui';
import type { AccentTokens, ResolvedTheme } from './themeColors';

const borderRadius = '10px';
export function createComponentTheme(tokens: AccentTokens, mode: ResolvedTheme = 'light', mobile = false): GlobalThemeOverrides {
  const closeButtonTheme = { closeBorderRadius: borderRadius };
  const commonTheme = (mode === 'dark' ? darkTheme : lightTheme).common;
  const controlFont = { fontSizeTiny: '16px', fontSizeSmall: '16px', fontSizeMedium: '16px', fontSizeLarge: '16px' };
  return {
    ...(mobile ? {
      Button: controlFont,
      Input: controlFont,
      InternalSelection: controlFont,
      InternalSelectMenu: controlFont,
    } : {}),
    common: {
      ...(mode === 'light' ? { bodyColor: '#f5f5f5' } : {}),
      borderRadius,
      primaryColor: tokens.text,
      primaryColorHover: tokens.textHover,
      primaryColorPressed: tokens.text,
      primaryColorSuppl: tokens.solid,
    },
    Layout: {
      headerColor: mode === 'dark' ? '#1a1a1f' : '#fff',
      siderColor: mode === 'dark' ? '#1a1a1f' : '#fff',
    },
    Card: {
      ...closeButtonTheme,
      paddingMedium: 'clamp(16px,3vw,19px) clamp(16px,3vw,24px) clamp(16px,3vw,20px)',
      colorEmbedded: 'var(--surface-subtle)',
      colorEmbeddedModal: 'var(--surface-subtle)',
      colorEmbeddedPopover: 'var(--surface-subtle)',
    },
    Dialog: closeButtonTheme,
    Drawer: closeButtonTheme,
    Message: {
      maxWidth: 'min(720px, calc(100vw - 32px))',
    },
    Tooltip: {
      color: commonTheme.popoverColor,
      textColor: commonTheme.textColor2,
    },
    Menu: {
      borderRadius,
    },
  };
}
