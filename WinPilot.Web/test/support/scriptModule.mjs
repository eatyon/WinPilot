import { readFileSync } from 'node:fs';
import { compileScript, parse } from '@vue/compiler-sfc';
import ts from 'typescript';

const compiledScripts = new Map();

function transpile(source) {
  return ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
  }).outputText;
}

export function loadScriptModule(url, members) {
  const cacheKey = JSON.stringify([url.href, members]);
  if (compiledScripts.has(cacheKey)) return compiledScripts.get(cacheKey);
  const raw = readFileSync(url, 'utf8');
  const source = url.pathname.endsWith('.vue')
    ? parse(raw, { filename: url.pathname }).descriptor.scriptSetup?.content
    : raw;
  if (source === undefined) throw new Error(`Missing TypeScript setup script: ${url}`);
  const compiled = transpile(source + (members ? `\nexport { ${members} };` : ''));
  compiledScripts.set(cacheKey, compiled);
  return compiled;
}

export function loadComponentModule(url) {
  const cacheKey = JSON.stringify([url.href, null, 'component']);
  if (compiledScripts.has(cacheKey)) return compiledScripts.get(cacheKey);
  const { descriptor } = parse(readFileSync(url, 'utf8'));
  const script = compileScript(descriptor, { id: 'test-component', inlineTemplate: true });
  const compiled = transpile(script.content);
  compiledScripts.set(cacheKey, compiled);
  return compiled;
}
