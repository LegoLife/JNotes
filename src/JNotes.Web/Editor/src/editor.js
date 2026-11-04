// Thin Tiptap wrapper used by MarkdownEditor.razor via JS interop.
// Markdown is the source of truth: content goes in as markdown and comes back out as markdown.
import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { Markdown } from '@tiptap/markdown';
import { Placeholder } from '@tiptap/extensions';
import Image from '@tiptap/extension-image';
import Youtube from '@tiptap/extension-youtube';
import { TableKit } from '@tiptap/extension-table';
import { TaskList, TaskItem } from '@tiptap/extension-list';
import FileHandler from '@tiptap/extension-file-handler';

const IMAGE_TYPES = ['image/png', 'image/jpeg', 'image/gif', 'image/webp'];

// A YouTube URL alone on its own line <-> an embedded player.
const YOUTUBE_LINE = /^(https?:\/\/(?:www\.|m\.)?(?:youtube\.com\/(?:watch\?\S*?v=|shorts\/|live\/|embed\/)|youtu\.be\/)[\w-]+\S*)[ \t]*(?:\n|$)/;
const YOUTUBE_LINE_START = /(^|\n)https?:\/\/(?:www\.|m\.)?(?:youtube\.com|youtu\.be)\//;

const YouTubeEmbed = Youtube.extend({
  markdownTokenName: 'youtube',
  markdownTokenizer: {
    name: 'youtube',
    level: 'block',
    start: (src) => {
      const m = YOUTUBE_LINE_START.exec(src);
      return m ? m.index + m[1].length : -1;
    },
    tokenize: (src) => {
      const m = YOUTUBE_LINE.exec(src);
      return m ? { type: 'youtube', raw: m[0], src: m[1] } : undefined;
    },
  },
  parseMarkdown: (token) => ({ type: 'youtube', attrs: { src: token.src } }),
  renderMarkdown: (node) => node.attrs.src,
}).configure({ nocookie: true, width: 640, height: 360 });

async function upload(file) {
  const res = await fetch('/attachments', {
    method: 'POST',
    body: file,
    headers: { 'Content-Type': file.type || 'application/octet-stream' },
    credentials: 'same-origin',
  });
  if (!res.ok) throw new Error((await res.text()) || `Upload failed (${res.status})`);
  return (await res.json()).url;
}

/**
 * @param {HTMLElement} element      host element
 * @param {any} dotnetRef            DotNetObjectReference exposing OnContentChanged(string), OnError(string)
 * @param {string} markdown          initial content
 * @param {{debounceMs?: number, fileInput?: HTMLInputElement}} options
 */
export function create(element, dotnetRef, markdown, options) {
  const debounceMs = options?.debounceMs ?? 1000;
  let timer = null;
  let dirty = false;
  let destroyed = false;

  const reportError = (e) => {
    console.error(e);
    if (!destroyed) dotnetRef.invokeMethodAsync('OnError', String(e?.message ?? e)).catch(() => {});
  };

  const insertImages = (editor, files, pos) => {
    for (const file of files) {
      upload(file)
        .then((url) => {
          const at = pos ?? editor.state.selection.anchor;
          editor.chain().insertContentAt(at, { type: 'image', attrs: { src: url, alt: file.name } }).focus().run();
        })
        .catch(reportError);
    }
  };

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
      Placeholder.configure({ placeholder: 'Start writing… (paste a YouTube link or an image to embed it)' }),
      Image.configure({ allowBase64: false }),
      YouTubeEmbed,
      TableKit.configure({ table: { resizable: false } }),
      TaskList,
      TaskItem.configure({ nested: true }),
      FileHandler.configure({
        allowedMimeTypes: IMAGE_TYPES,
        onDrop: (ed, files, pos) => insertImages(ed, files, pos),
        onPaste: (ed, files, html) => {
          if (html) return false; // copied from a web page: keep the page's own <img>
          insertImages(ed, files);
        },
      }),
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

  // Toolbar "image" button is a <label> wrapping this input (keeps the click a real user gesture).
  const fileInput = options?.fileInput;
  const onFilePicked = () => {
    const files = [...(fileInput.files ?? [])].filter((f) => IMAGE_TYPES.includes(f.type));
    insertImages(editor, files);
    fileInput.value = '';
  };
  fileInput?.addEventListener('change', onFilePicked);

  // Save promptly when the tab is hidden/closed.
  const onVisibility = () => {
    if (document.visibilityState === 'hidden') flush().catch(() => {});
  };
  document.addEventListener('visibilitychange', onVisibility);

  const commands = {
    bold: () => editor.chain().focus().toggleBold().run(),
    italic: () => editor.chain().focus().toggleItalic().run(),
    strike: () => editor.chain().focus().toggleStrike().run(),
    code: () => editor.chain().focus().toggleCode().run(),
    h1: () => editor.chain().focus().toggleHeading({ level: 1 }).run(),
    h2: () => editor.chain().focus().toggleHeading({ level: 2 }).run(),
    h3: () => editor.chain().focus().toggleHeading({ level: 3 }).run(),
    bulletList: () => editor.chain().focus().toggleBulletList().run(),
    orderedList: () => editor.chain().focus().toggleOrderedList().run(),
    taskList: () => editor.chain().focus().toggleTaskList().run(),
    blockquote: () => editor.chain().focus().toggleBlockquote().run(),
    codeBlock: () => editor.chain().focus().toggleCodeBlock().run(),
    hr: () => editor.chain().focus().setHorizontalRule().run(),
    table: () => editor.chain().focus().insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run(),
    undo: () => editor.chain().focus().undo().run(),
    redo: () => editor.chain().focus().redo().run(),
    youtube: (url) => editor.chain().focus().setYoutubeVideo({ src: url }).run(),
  };

  return {
    flush,
    exec: (name, arg) => (commands[name] ? commands[name](arg) : false),
    getMarkdown: () => editor.getMarkdown(),
    focus: () => { editor.commands.focus('end'); },
    destroy: async () => {
      document.removeEventListener('visibilitychange', onVisibility);
      fileInput?.removeEventListener('change', onFilePicked);
      try { await flush(); } finally {
        destroyed = true;
        editor.destroy();
      }
    },
  };
}

// Exposed for a quick round-trip check (see README); not used by the app.
export function roundTrip(markdown) {
  const el = document.createElement('div');
  const ed = new Editor({
    element: el,
    extensions: [StarterKit, Markdown, Image, YouTubeEmbed, TableKit, TaskList, TaskItem.configure({ nested: true })],
    content: markdown,
    contentType: 'markdown',
  });
  const out = ed.getMarkdown();
  ed.destroy();
  return out;
}
