import { readFileSync } from 'node:fs';
import { parse } from '@vue/compiler-sfc';
import ts from 'typescript';

export function loadScriptModule(url, members) {
  const raw = readFileSync(url, 'utf8');
  const source = url.pathname.endsWith('.vue')
    ? parse(raw, { filename: url.pathname }).descriptor.scriptSetup?.content
    : raw;
  if (source === undefined) throw new Error(`Missing TypeScript setup script: ${url}`);
  return ts.transpileModule(source + (members ? `\nexport { ${members} };` : ''), {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
  }).outputText;
}
