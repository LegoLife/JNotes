// Thin Tiptap wrapper used by MarkdownEditor.razor via JS interop.
// Markdown is the source of truth: content goes in as markdown and comes back out as markdown.
import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { Markdown } from '@tiptap/markdown';
import { Placeholder } from '@tiptap/extensions';

/**
 * @param {HTMLElement} element  host element
 * @param {any} dotnetRef        DotNetObjectReference exposing OnContentChanged(string)
 * @param {string} markdown      initial content
 * @param {{debounceMs?: number}} options
 */
export function create(element, dotnetRef, markdown, options) {
  const debounceMs = options?.debounceMs ?? 1000;
  let timer = null;
  let dirty = false;
  let destroyed = false;

  const flush = async () => {
    clearTimeout(timer);
    timer = null;
    if (!dirty || destroyed) return;
    dirty = false;
    await dotnetRef.invokeMethodAsync('OnContentChanged', editor.getMarkdown());
  };

  const editor = new Editor({
    element,
    extensions: [
      StarterKit.configure({ link: { openOnClick: false, autolink: true } }),
      Markdown,
      Placeholder.configure({ placeholder: 'Start writing…' }),
    ],
    content: markdown ?? '',
    contentType: 'markdown',
    onUpdate: () => {
      dirty = true;
      clearTimeout(timer);
      timer = setTimeout(() => flush().catch(console.error), debounceMs);
    },
    onBlur: () => flush().catch(console.error),
  });

  // Save promptly when the tab is hidden/closed.
  const onVisibility = () => {
    if (document.visibilityState === 'hidden') flush().catch(() => {});
  };
  document.addEventListener('visibilitychange', onVisibility);

  return {
    flush,
    getMarkdown: () => editor.getMarkdown(),
    focus: () => { editor.commands.focus('end'); },
    destroy: async () => {
      document.removeEventListener('visibilitychange', onVisibility);
      try { await flush(); } finally {
        destroyed = true;
        editor.destroy();
      }
    },
  };
}
