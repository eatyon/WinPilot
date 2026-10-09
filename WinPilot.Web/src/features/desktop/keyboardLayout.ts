export const functionKeys = ['Escape', 'F1', 'F2', 'F3', 'F4', 'F5', 'F6', 'F7', 'F8', 'F9', 'F10', 'F11', 'F12']
export const mainKeyboardRows = [
  ['OemTilde', 'D1', 'D2', 'D3', 'D4', 'D5', 'D6', 'D7', 'D8', 'D9', 'D0', 'OemMinus', 'OemPlus', 'Backspace'],
  ['Tab', 'Q', 'W', 'E', 'R', 'T', 'Y', 'U', 'I', 'O', 'P', 'OemOpenBrackets', 'OemCloseBrackets', 'OemPipe'],
  ['CapsLock', 'A', 'S', 'D', 'F', 'G', 'H', 'J', 'K', 'L', 'OemSemicolon', 'OemQuotes', 'Enter'],
  ['Shift', 'Z', 'X', 'C', 'V', 'B', 'N', 'M', 'OemComma', 'OemPeriod', 'OemQuestion'],
  ['Ctrl', 'Win', 'Alt', 'Space', 'Apps'],
]
export const navigationRows = [['PrintScreen', 'ScrollLock', 'Pause'], ['Insert', 'Home', 'PageUp'], ['Delete', 'End', 'PageDown'], ['Left', 'Up', 'Down', 'Right']]
export const numpadRows = [['NumLock', 'NumpadDivide', 'NumpadMultiply', 'NumpadSubtract'], ['Numpad7', 'Numpad8', 'Numpad9', 'NumpadAdd'], ['Numpad4', 'Numpad5', 'Numpad6'], ['Numpad1', 'Numpad2', 'Numpad3', 'NumpadEnter'], ['Numpad0', 'NumpadDecimal']]
export const wideKeys = ['Tab', 'CapsLock', 'Shift', 'Backspace', 'Enter', 'Space', 'NumpadEnter']
export const keyLabel = (key: string) => ({ CapsLock: 'Caps Lock', NumLock: 'Num Lock', ScrollLock: 'Scroll Lock', Escape: 'Esc', PrintScreen: 'PrtSc', Pause: 'Pause', OemTilde: '`', OemMinus: '-', OemPlus: '=', OemOpenBrackets: '[', OemCloseBrackets: ']', OemPipe: '\\', OemSemicolon: ';', OemQuotes: "'", OemComma: ',', OemPeriod: '.', OemQuestion: '/', NumpadDivide: '/', NumpadMultiply: '*', NumpadSubtract: '-', NumpadAdd: '+', NumpadDecimal: '.', NumpadEnter: 'Enter' } as Record<string, string>)[key] ?? (key.startsWith('D') && key.length === 2 ? key[1] : key)
