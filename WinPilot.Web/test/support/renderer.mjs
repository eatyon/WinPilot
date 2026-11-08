import { createRenderer } from 'vue';

export function createTestRenderer() {
  const node = (text = '') => ({ children: [], parent: null, text, props: {}, style: {} });
  const root = node();
  const remove = child => {
    if (child.parent) child.parent.children.splice(child.parent.children.indexOf(child), 1);
    child.parent = null;
  };
  const find = (target, predicate) => predicate(target)
    ? target
    : target.children.map(child => find(child, predicate)).find(Boolean);
  const renderer = createRenderer({
    createElement: () => node(), createText: node, createComment: node,
    setText: (target, text) => { target.text = text; },
    setElementText(target, text) {
      for (const child of target.children) child.parent = null;
      target.children = [];
      target.text = text;
    },
    patchProp: (target, key, previous, value) => { target.props[key] = value; },
    insert(child, parent, anchor) {
      if (child === anchor) return;
      remove(child);
      const index = anchor ? parent.children.indexOf(anchor) : parent.children.length;
      if (index < 0) throw new Error('Renderer anchor does not belong to its parent');
      parent.children.splice(index, 0, child);
      child.parent = parent;
    },
    remove, parentNode: target => target.parent,
    nextSibling: target => target.parent?.children[target.parent.children.indexOf(target) + 1] ?? null,
  });
  return { renderer, root, find: predicate => find(root, predicate) };
}
