export const accentOptions = [
  { value: 'blue', label: '蓝色', color: '#1c77c7', light: { text: '#0067c0', textHover: '#005ba8' }, dark: { text: '#4aa3e8', textHover: '#6cb5ee' } },
  { value: 'violet', label: '紫色', color: '#7b63cb', light: { text: '#6a4fc4', textHover: '#5b43ab' }, dark: { text: '#a48be0', textHover: '#b8a2e8' } },
  { value: 'teal', label: '青色', color: '#278178', light: { text: '#0d7268', textHover: '#0b6259' }, dark: { text: '#3fb8ab', textHover: '#57c9bd' } },
  { value: 'brown', label: '可可棕', color: '#795548', light: { text: '#795548', textHover: '#65463c' }, dark: { text: '#c9a99a', textHover: '#dbc0b3' } },
  { value: 'plum', label: '梅紫', color: '#a052b4', light: { text: '#88359e', textHover: '#762b89' }, dark: { text: '#ce8fe0', textHover: '#dca6e9' } },
  { value: 'pink', label: '粉色', color: '#c4418f', light: { text: '#b81b78', textHover: '#a01668' }, dark: { text: '#ea6eb2', textHover: '#ec78b6' } },
  { value: 'slate', label: '石板灰', color: '#65758b', light: { text: '#526175', textHover: '#435165' }, dark: { text: '#acbcd0', textHover: '#c2cfe0' } },
  { value: 'lime', label: '青柠', color: '#6a7b1e', light: { text: '#596c12', textHover: '#4b5c0e' }, dark: { text: '#bacb64', textHover: '#cbdc7b' } },
  { value: 'indigo', label: '靛蓝', color: '#526dd1', light: { text: '#3d5ccc', textHover: '#3350b3' }, dark: { text: '#7d95ea', textHover: '#95a9ef' } },
  { value: 'amber', label: '琥珀色', color: '#a16819', light: { text: '#9a5d08', textHover: '#835006' }, dark: { text: '#d9a32b', textHover: '#e3b74e' } },
] as const;

export type AccentColor = typeof accentOptions[number]['value'];
export type ResolvedTheme = 'light' | 'dark';
export type AccentTokens = { text: string; textHover: string; solid: string };

export function getAccentTokens(color: AccentColor, mode: ResolvedTheme): AccentTokens {
  const option = accentOptions.find(option => option.value === color) ?? accentOptions[0];
  return { ...option[mode], solid: option.color };
}
