import { nextTick } from 'vue';
import { darkTheme } from 'naive-ui';
import type { FitAddon } from '@xterm/addon-fit';
import type { Terminal } from '@xterm/xterm';

const terminalFontSize = 15;

interface TerminalRenderer {
  terminal: Terminal;
  fitAddon: FitAddon;
  columns: number;
  rows: number;
}

interface TerminalRendererOptions {
  isActive: () => boolean;
  hasSession: (id: string) => boolean;
  activeId: () => string;
  onResize: (id: string, columns: number, rows: number) => void;
}

// 每个终端按 DOM 挂载情况创建渲染器，挂载前暂存输出，页面退出时统一释放观察器和渲染器。
export function createTerminalRenderers(options: TerminalRendererOptions) {
  const terminalHosts = new Map<string, HTMLElement>();
  const renderers = new Map<string, TerminalRenderer>();
  const pendingOutput = new Map<string, string[]>();
  let resizeObserver: ResizeObserver | undefined;
  let runtime: { Terminal: typeof Terminal; FitAddon: typeof FitAddon } | undefined;
  let loadingRuntime: Promise<void> | undefined;

  function prepare(): Promise<void> {
    if (runtime) return Promise.resolve();
    loadingRuntime ??= Promise.all([
      import('@xterm/xterm'),
      import('@xterm/addon-fit'),
      import('@xterm/xterm/css/xterm.css'),
      typeof document !== 'undefined' && document.fonts
        ? document.fonts.load(`${terminalFontSize}px "v-mono"`).catch(() => undefined)
        : Promise.resolve(),
    ]).then(([terminal, fit]) => {
      runtime = { Terminal: terminal.Terminal, FitAddon: fit.FitAddon };
    }).finally(() => {
      loadingRuntime = undefined;
    });
    return loadingRuntime;
  }

  function setTerminalHost(id: string, element: unknown) {
    if (element instanceof HTMLElement) {
      terminalHosts.set(id, element);
      void nextTick(() => ensureRenderer(id));
    } else {
      const host = terminalHosts.get(id);
      if (host) resizeObserver?.unobserve(host);
      terminalHosts.delete(id);
    }
  }

  function ensureRenderer(id: string) {
    const host = terminalHosts.get(id);
    if (!runtime || !options.isActive() || !options.hasSession(id) || !host || renderers.has(id))
      return;

    const terminal = new runtime.Terminal({
      convertEol: false,
      cursorBlink: false,
      cursorStyle: 'bar',
      cursorInactiveStyle: 'none',
      disableStdin: true,
      fontFamily: darkTheme.common.fontFamilyMono,
      fontSize: terminalFontSize,
      lineHeight: 1.35,
      scrollback: 5000,
      theme: {
        background: darkTheme.common.cardColor,
        foreground: darkTheme.common.textColor2,
        cursor: darkTheme.common.textColor1,
        selectionBackground: darkTheme.common.textColor3,
      },
    });
    const fitAddon = new runtime.FitAddon();
    terminal.loadAddon(fitAddon);
    terminal.open(host);
    if (terminal.textarea) terminal.textarea.tabIndex = -1;
    terminal.attachCustomKeyEventHandler(event => event.key !== 'Tab');
    renderers.set(id, { terminal, fitAddon, columns: 0, rows: 0 });

    const output = pendingOutput.get(id);
    if (output) {
      for (const chunk of output) terminal.write(chunk);
      pendingOutput.delete(id);
    }

    resizeObserver ??= new ResizeObserver(() => fitActiveTerminal());
    resizeObserver.observe(host);
    if (id === options.activeId()) fitActiveTerminal();
  }

  function writeOutput(id: string, content: string) {
    if (!options.isActive() || !options.hasSession(id)) return;
    const renderer = renderers.get(id);
    if (renderer) {
      renderer.terminal.write(content);
      return;
    }
    const output = pendingOutput.get(id) ?? [];
    output.push(content);
    pendingOutput.set(id, output);
    void nextTick(() => ensureRenderer(id));
  }

  function fitActiveTerminal() {
    const renderer = renderers.get(options.activeId());
    const host = terminalHosts.get(options.activeId());
    if (!renderer || !host || host.clientWidth === 0 || host.clientHeight === 0) return;
    try {
      renderer.fitAddon.fit();
      if (renderer.columns !== renderer.terminal.cols || renderer.rows !== renderer.terminal.rows) {
        renderer.columns = renderer.terminal.cols;
        renderer.rows = renderer.terminal.rows;
        options.onResize(options.activeId(), renderer.columns, renderer.rows);
      }
    } catch {}
  }

  function bufferOutput(id: string, chunks: string[]) {
    pendingOutput.set(id, chunks);
  }

  function remove(id: string) {
    const renderer = renderers.get(id);
    if (renderer) {
      renderer.terminal.dispose();
      renderers.delete(id);
    }
    const host = terminalHosts.get(id);
    if (host) resizeObserver?.unobserve(host);
    terminalHosts.delete(id);
    pendingOutput.delete(id);
  }

  function dispose() {
    resizeObserver?.disconnect();
    resizeObserver = undefined;
    for (const renderer of renderers.values()) renderer.terminal.dispose();
    renderers.clear();
    terminalHosts.clear();
    pendingOutput.clear();
  }

  return { prepare, setTerminalHost, writeOutput, fitActiveTerminal, bufferOutput, remove, dispose };
}
