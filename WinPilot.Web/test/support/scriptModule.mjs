import { readFileSync } from 'node:fs';
import ts from 'typescript';

export function loadScriptModule(url, members) {
  const raw = readFileSync(url, 'utf8');
  const source = url.pathname.endsWith('.vue')
    ? raw.match(/<script setup lang="ts">([\s\S]*?)<\/script>/)?.[1]
    : raw;
  if (source === undefined) throw new Error(`Missing TypeScript setup script: ${url}`);
  return ts.transpileModule(source + (members ? `\nexport { ${members} };` : ''), {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
  }).outputText;
}
