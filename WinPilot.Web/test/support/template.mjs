import { parse } from '@vue/compiler-sfc';
import { parse as parseTemplate } from '@vue/compiler-dom';

export function elements(source, predicate) {
  const template = parse(source).descriptor.template;
  if (!template) return [];
  const result = [];
  function visit(node) {
    if (node.type === 1 && predicate(node)) result.push(node);
    for (const child of node.children ?? []) visit(child);
  }
  visit(parseTemplate(template.content));
  return result;
}

export const attribute = (node, name) => node.props.find(prop => prop.type === 6 && prop.name === name)?.value?.content;
export const binding = (node, name) => node.props.find(prop => prop.type === 7 && prop.name === 'bind' && prop.arg?.content === name)?.exp?.content;
export const event = (node, name) => node.props.find(prop => prop.type === 7 && prop.name === 'on' && prop.arg?.content === name)?.exp?.content;
