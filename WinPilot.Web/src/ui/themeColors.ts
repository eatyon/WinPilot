export const accentOptions = [
  { value: 'blue', label: '蓝色' },
  { value: 'violet', label: '紫色' },
  { value: 'teal', label: '青色' },
  { value: 'brown', label: '可可棕' },
  { value: 'plum', label: '梅紫' },
  { value: 'pink', label: '粉色' },
  { value: 'slate', label: '石板灰' },
  { value: 'lime', label: '青柠' },
  { value: 'indigo', label: '靛蓝' },
  { value: 'amber', label: '琥珀色' },
] as const

export type AccentColor = typeof accentOptions[number]['value']
export type ResolvedTheme = 'light' | 'dark'
export type AccentTokens = {
  text: string
  textHover: string
  solid: string
  solidHover: string
  contrast: string
  bg: string
  border: string
  focusRing: string
}

export const accentPalettes: Record<AccentColor, Record<ResolvedTheme, AccentTokens>> = {
  blue: {
    light: {
      text: '#0067c0', textHover: '#005ba8',
      solid: '#1c77c7', solidHover: '#1372c5', contrast: '#ffffff',
      bg: '#e5f1fb', border: '#0067c0', focusRing: '#0067c0'
    },
    dark: {
      text: '#4aa3e8', textHover: '#6cb5ee',
      solid: '#1c77c7', solidHover: '#1372c5', contrast: '#ffffff',
      bg: '#17283a', border: '#4aa3e8', focusRing: '#4aa3e8'
    },
  },
  violet: {
    light: {
      text: '#6a4fc4', textHover: '#5b43ab',
      solid: '#7b63cb', solidHover: '#775ec9', contrast: '#ffffff',
      bg: '#f0edf7', border: '#6a4fc4', focusRing: '#6a4fc4'
    },
    dark: {
      text: '#a48be0', textHover: '#b8a2e8',
      solid: '#7b63cb', solidHover: '#775ec9', contrast: '#ffffff',
      bg: '#2b2440', border: '#a48be0', focusRing: '#a48be0'
    },
  },
  teal: {
    light: {
      text: '#0d7268', textHover: '#0b6259',
      solid: '#278178', solidHover: '#1f7d73', contrast: '#ffffff',
      bg: '#e7f3f2', border: '#0d7268', focusRing: '#0d7268'
    },
    dark: {
      text: '#3fb8ab', textHover: '#57c9bd',
      solid: '#278178', solidHover: '#1f7d73', contrast: '#ffffff',
      bg: '#193735', border: '#3fb8ab', focusRing: '#3fb8ab'
    },
  },
  brown: {
    light: {
      text: '#795548', textHover: '#65463c',
      solid: '#795548', solidHover: '#6b4a3e', contrast: '#ffffff',
      bg: '#f4eeeb', border: '#795548', focusRing: '#795548'
    },
    dark: {
      text: '#c9a99a', textHover: '#dbc0b3',
      solid: '#795548', solidHover: '#6b4a3e', contrast: '#ffffff',
      bg: '#352923', border: '#c9a99a', focusRing: '#c9a99a'
    },
  },
  plum: {
    light: {
      text: '#88359e', textHover: '#762b89',
      solid: '#a052b4', solidHover: '#994aaf', contrast: '#ffffff',
      bg: '#f5edf8', border: '#88359e', focusRing: '#88359e'
    },
    dark: {
      text: '#ce8fe0', textHover: '#dca6e9',
      solid: '#a052b4', solidHover: '#994aaf', contrast: '#ffffff',
      bg: '#34233b', border: '#ce8fe0', focusRing: '#ce8fe0'
    },
  },
  pink: {
    light: {
      text: '#b81b78', textHover: '#a01668',
      solid: '#c4418f', solidHover: '#c1398a', contrast: '#ffffff',
      bg: '#fceff3', border: '#b81b78', focusRing: '#b81b78'
    },
    dark: {
      text: '#ea6eb2', textHover: '#ec78b6',
      solid: '#c4418f', solidHover: '#c1398a', contrast: '#ffffff',
      bg: '#3d2031', border: '#ea6eb2', focusRing: '#ea6eb2'
    },
  },
  slate: {
    light: {
      text: '#526175', textHover: '#435165',
      solid: '#65758b', solidHover: '#5c6c82', contrast: '#ffffff',
      bg: '#edf1f5', border: '#526175', focusRing: '#526175'
    },
    dark: {
      text: '#acbcd0', textHover: '#c2cfe0',
      solid: '#65758b', solidHover: '#5c6c82', contrast: '#ffffff',
      bg: '#28313e', border: '#acbcd0', focusRing: '#acbcd0'
    },
  },
  lime: {
    light: {
      text: '#596c12', textHover: '#4b5c0e',
      solid: '#6a7b1e', solidHover: '#67781b', contrast: '#ffffff',
      bg: '#f1f5e5', border: '#596c12', focusRing: '#596c12'
    },
    dark: {
      text: '#bacb64', textHover: '#cbdc7b',
      solid: '#6a7b1e', solidHover: '#67781b', contrast: '#ffffff',
      bg: '#2b321d', border: '#bacb64', focusRing: '#bacb64'
    },
  },
  indigo: {
    light: {
      text: '#3d5ccc', textHover: '#3350b3',
      solid: '#526dd1', solidHover: '#4c69d0', contrast: '#ffffff',
      bg: '#edf0f8', border: '#3d5ccc', focusRing: '#3d5ccc'
    },
    dark: {
      text: '#7d95ea', textHover: '#95a9ef',
      solid: '#526dd1', solidHover: '#4c69d0', contrast: '#ffffff',
      bg: '#242942', border: '#7d95ea', focusRing: '#7d95ea'
    },
  },
  amber: {
    light: {
      text: '#9a5d08', textHover: '#835006',
      solid: '#a16819', solidHover: '#9d6310', contrast: '#ffffff',
      bg: '#f7f1e5', border: '#9a5d08', focusRing: '#9a5d08'
    },
    dark: {
      text: '#d9a32b', textHover: '#e3b74e',
      solid: '#a16819', solidHover: '#9d6310', contrast: '#ffffff',
      bg: '#3c3118', border: '#d9a32b', focusRing: '#d9a32b'
    },
  },
}

export function getAccentTokens(color: AccentColor, theme: ResolvedTheme): AccentTokens {
  return accentPalettes[color][theme]
}
