export async function renderMarkdown(source: string) {
  const [{ default: MarkdownIt }, { default: DOMPurify }] = await Promise.all([
    import('markdown-it'),
    import('dompurify'),
  ]);
  const markdown = new MarkdownIt({ html: false, linkify: false });
  markdown.validateLink = (url) => /^https?:\/\//i.test(url) || /^mailto:/i.test(url);
  markdown.renderer.rules.image = (tokens, index) => markdown.utils.escapeHtml(tokens[index].content);
  const renderLink =
    markdown.renderer.rules.link_open ??
    ((tokens, index, options, _environment, self) => self.renderToken(tokens, index, options));
  markdown.renderer.rules.link_open = (tokens, index, options, environment, self) => {
    tokens[index].attrSet('target', '_blank');
    tokens[index].attrSet('rel', 'noopener noreferrer');
    return renderLink(tokens, index, options, environment, self);
  };
  return DOMPurify.sanitize(markdown.render(source), {
    FORBID_TAGS: ['img', 'svg', 'iframe', 'form', 'input', 'button'],
  });
}
