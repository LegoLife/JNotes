// Thin Tiptap wrapper used by MarkdownEditor.razor via JS interop.
// Markdown is the source of truth: content goes in as markdown and comes back out as markdown.
import { Editor, Extension, Node, mergeAttributes, textblockTypeInputRule } from '@tiptap/core';
import { Plugin, PluginKey } from '@tiptap/pm/state';
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

// ---------- ✨ AI blocks ----------
// Same syntax as JNotes.Core/AiBlocks.cs:
//   :::ai-request{id="k3f9a2"}\nprompt\n:::        (pending, typed with /ai)
//   :::ai-result{id="k3f9a2" prompt="…"}\n…\n:::   (filled in by Claude, Accept/Discard)
const AI_BLOCK = /^:::ai-(request|result)\{([^}\n]*)\}[ \t]*\n([\s\S]*?)\n:::[ \t]*(?:\n|$)/;
const AI_BLOCK_START = /(^|\n):::ai-(?:request|result)\{/;
const escapeAttr = (s) => String(s ?? '').replace(/\n/g, ' ').replace(/&/g, '&amp;').replace(/"/g, '&quot;');
const unescapeAttr = (s) => s.replace(/&quot;/g, '"').replace(/&amp;/g, '&');
const parseAttrs = (s) => Object.fromEntries([...s.matchAll(/(\w+)="([^"]*)"/g)].map((m) => [m[1], unescapeAttr(m[2])]));
const newRequestId = () => Math.random().toString(36).slice(2, 8);

function aiTokenizer(kind) {
  return {
    name: `ai-${kind}`,
    level: 'block',
    start: (src) => {
      const m = AI_BLOCK_START.exec(src);
      return m ? m.index + m[1].length : -1;
    },
    tokenize: (src, _tokens, lexer) => {
      const m = AI_BLOCK.exec(src);
      if (!m || m[1] !== kind) return undefined;
      const body = m[3];
      return {
        type: `ai-${kind}`, raw: m[0], attrs: parseAttrs(m[2]), text: body,
        tokens: kind === 'request' ? lexer.inlineTokens(body.trim()) : lexer.blockTokens(body),
      };
    },
  };
}

const AiRequest = Node.create({
  name: 'aiRequest',
  group: 'block',
  content: 'inline*',
  defining: true,
  addOptions: () => ({ onGenerate: null }),
  addAttributes: () => ({
    id: { default: null, parseHTML: (el) => el.getAttribute('data-id') },
    // UI-only state ('generating' | 'failed'); never written to markdown.
    status: { default: null, rendered: false, keepOnSplit: false },
  }),
  parseHTML: () => [{ tag: 'div[data-ai-request]' }],
  renderHTML: ({ node, HTMLAttributes }) =>
    ['div', mergeAttributes(HTMLAttributes, {
      'data-ai-request': '', 'data-id': node.attrs.id, class: 'ai-request',
      ...(node.attrs.status ? { 'data-status': node.attrs.status } : {}),
    }), 0],
  markdownTokenName: 'ai-request',
  markdownTokenizer: aiTokenizer('request'),
  parseMarkdown: (token, h) => ({ type: 'aiRequest', attrs: { id: token.attrs.id ?? newRequestId() }, content: h.parseInline(token.tokens) }),
  renderMarkdown: (node, h) => `:::ai-request{id="${escapeAttr(node.attrs.id)}"}\n${h.renderChildren(node.content ?? [])}\n:::`,
  addInputRules() {
    // "/ai " at the start of an empty line turns it into a request block.
    return [textblockTypeInputRule({ find: /^\/ai\s$/, type: this.type, getAttributes: () => ({ id: newRequestId() }) })];
  },
  addKeyboardShortcuts() {
    return {
      // Enter sends the request to Claude (if there's a prompt) and continues writing below it.
      Enter: ({ editor }) => {
        const { $from } = editor.state.selection;
        const node = $from.parent;
        if (node.type.name !== this.name) return false;
        const pos = $from.before();
        const after = $from.after();
        const canGenerate = this.options.onGenerate && node.textContent.trim().length > 0 && node.attrs.status !== 'generating';
        if (canGenerate) setRequestStatus(editor, node.attrs.id, 'generating');
        editor.chain().insertContentAt(after, { type: 'paragraph' }).setTextSelection(after + 1).run();
        if (canGenerate) this.options.onGenerate(node.attrs.id, pos);
        return true;
      },
    };
  },
});

const AiResult = Node.create({
  name: 'aiResult',
  group: 'block',
  content: 'block+',
  defining: true,
  addAttributes: () => ({
    id: { default: null, parseHTML: (el) => el.getAttribute('data-id') },
    prompt: { default: '', parseHTML: (el) => el.getAttribute('data-prompt') ?? '' },
  }),
  parseHTML: () => [{ tag: 'div[data-ai-result]' }],
  renderHTML: ({ node, HTMLAttributes }) =>
    ['div', mergeAttributes(HTMLAttributes, { 'data-ai-result': '', 'data-id': node.attrs.id, 'data-prompt': node.attrs.prompt }), 0],
  markdownTokenName: 'ai-result',
  markdownTokenizer: aiTokenizer('result'),
  parseMarkdown: (token, h) => ({
    type: 'aiResult',
    attrs: { id: token.attrs.id ?? null, prompt: token.attrs.prompt ?? '' },
    content: h.parseChildren(token.tokens),
  }),
  renderMarkdown: (node, h) =>
    `:::ai-result{id="${escapeAttr(node.attrs.id)}" prompt="${escapeAttr(node.attrs.prompt)}"}\n${h.renderChildren(node.content ?? [], '\n\n')}\n:::`,
  addNodeView() {
    return ({ node, getPos, editor }) => {
      const dom = document.createElement('div');
      dom.className = 'ai-result';
      const header = document.createElement('div');
      header.className = 'ai-result-header';
      header.contentEditable = 'false';
      const badge = document.createElement('span');
      badge.className = 'ai-badge';
      badge.textContent = '✨ Claude';
      const prompt = document.createElement('span');
      prompt.className = 'ai-prompt';
      prompt.textContent = node.attrs.prompt;
      const accept = document.createElement('button');
      accept.type = 'button';
      accept.className = 'ai-accept';
      accept.textContent = 'Accept';
      accept.title = 'Keep this as normal text';
      const discard = document.createElement('button');
      discard.type = 'button';
      discard.className = 'ai-discard';
      discard.textContent = 'Discard';
      discard.title = 'Remove this generated text';
      header.append(badge, prompt, accept, discard);
      const body = document.createElement('div');
      body.className = 'ai-result-body';
      dom.append(header, body);

      const act = (keep) => {
        const pos = getPos();
        if (typeof pos !== 'number') return;
        const current = editor.state.doc.nodeAt(pos);
        if (!current) return;
        editor.chain().focus().command(({ tr }) => {
          if (keep) tr.replaceWith(pos, pos + current.nodeSize, current.content);
          else tr.delete(pos, pos + current.nodeSize);
          return true;
        }).run();
      };
      accept.addEventListener('click', () => act(true));
      discard.addEventListener('click', () => act(false));

      return {
        dom,
        contentDOM: body,
        update: (updated) => {
          if (updated.type.name !== 'aiResult') return false;
          prompt.textContent = updated.attrs.prompt;
          return true;
        },
        stopEvent: (e) => header.contains(e.target),
        ignoreMutation: (m) => header.contains(m.target),
      };
    };
  },
});

/** Sets the UI-only status of a request box (not part of undo history). Returns false if it isn't found. */
function setRequestStatus(editor, requestId, status) {
  let found = false;
  const tr = editor.state.tr;
  editor.state.doc.descendants((node, pos) => {
    if (found) return false;
    if (node.type.name === 'aiRequest' && node.attrs.id === requestId) {
      tr.setNodeMarkup(pos, undefined, { ...node.attrs, status });
      found = true;
      return false;
    }
    return true;
  });
  if (found) editor.view.dispatch(tr.setMeta('addToHistory', false));
  return found;
}

// ---------- applying Claude's edits to an open note (mirrors AiBlocks.cs) ----------

const headingText = (s) => s.trim().replace(/^#+\s*/, '').replace(/\s*#+$/, '').replace(/\s+/g, ' ').toLowerCase();

const remoteEdits = {
  // Replace the request block with the result block, leaving the rest of the document (and cursor) alone.
  fulfill(editor, { requestId, block }) {
    let target = null;
    editor.state.doc.descendants((node, pos) => {
      if (target) return false;
      if (node.type.name === 'aiRequest' && node.attrs.id === requestId) { target = { pos, size: node.nodeSize }; return false; }
      return true;
    });
    if (!target) return false;
    return editor.commands.insertContentAt({ from: target.pos, to: target.pos + target.size }, block, { contentType: 'markdown', updateSelection: false });
  },
  append(editor, { markdown }) {
    return editor.commands.insertContentAt(editor.state.doc.content.size, markdown, { contentType: 'markdown', updateSelection: false });
  },
  // Section = top-level content after the heading up to the next heading of the same or higher level.
  section(editor, { heading, markdown }) {
    const doc = editor.state.doc;
    let from = null; let to = doc.content.size; let level = 0;
    doc.forEach((node, offset) => {
      if (node.type.name !== 'heading') return;
      if (from === null && headingText(node.textContent) === headingText(heading)) {
        from = offset + node.nodeSize; level = node.attrs.level;
      } else if (from !== null && to === doc.content.size && offset >= from && node.attrs.level <= level) {
        to = offset;
      }
    });
    if (from === null) return false;
    return editor.commands.insertContentAt({ from, to }, markdown, { contentType: 'markdown', updateSelection: false });
  },
};

// ---------- [[ note links ----------
// Typing "[[" opens a picker of notes; choosing one inserts a normal markdown link [Title](/notes/{id}).
const NOTE_LINK_TRIGGER = /\[\[([^\[\]\n]{0,80})$/;

const NoteLinkPicker = Extension.create({
  name: 'noteLinkPicker',
  addOptions: () => ({ findNotes: null }),
  addProseMirrorPlugins() {
    const findNotes = this.options.findNotes;
    if (!findNotes) return [];
    const editor = this.editor;

    const popup = document.createElement('div');
    popup.className = 'note-link-picker';
    popup.setAttribute('role', 'listbox');
    popup.hidden = true;
    document.body.appendChild(popup);

    let state = null;       // { from, to, query } while "[[query" is right before the cursor
    let items = [];
    let selected = 0;
    let dismissedAt = null; // start of a trigger the user closed with Esc
    let seq = 0;

    const close = () => { state = null; items = []; seq++; popup.hidden = true; };

    const render = () => {
      popup.replaceChildren();
      if (items.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'nlp-empty';
        empty.textContent = state?.query ? 'No matching notes' : 'Type to search notes…';
        popup.append(empty);
      }
      items.forEach((item, i) => {
        const row = document.createElement('div');
        row.className = 'nlp-item' + (i === selected ? ' selected' : '');
        row.setAttribute('role', 'option');
        const title = document.createElement('span');
        title.className = 'nlp-title';
        title.textContent = item.title;
        const path = document.createElement('span');
        path.className = 'nlp-path';
        path.textContent = item.path;
        row.append(title, path);
        row.addEventListener('mousedown', (e) => { e.preventDefault(); choose(i); });
        row.addEventListener('mouseenter', () => { if (selected !== i) { selected = i; render(); } });
        popup.append(row);
      });
      popup.querySelector('.selected')?.scrollIntoView({ block: 'nearest' });
    };

    const position = (view) => {
      const c = view.coordsAtPos(state.from);
      popup.style.left = `${Math.max(8, Math.min(c.left, window.innerWidth - 328))}px`;
      popup.style.top = `${c.bottom + 4}px`;
    };

    const search = (query) => {
      const mine = ++seq;
      findNotes(query).then((result) => {
        if (mine !== seq || !state) return;
        items = result ?? [];
        selected = 0;
        render();
      }).catch(console.error);
    };

    const choose = (i) => {
      const item = items[i];
      if (!item || !state) return;
      const { from, to } = state;
      close();
      editor.chain().focus().insertContentAt({ from, to }, [
        { type: 'text', text: item.title, marks: [{ type: 'link', attrs: { href: `/notes/${item.id}` } }] },
        { type: 'text', text: ' ' },
      ]).unsetMark('link').run();
    };

    return [new Plugin({
      key: new PluginKey('noteLinkPicker'),
      view: () => ({
        update: (view) => {
          const sel = view.state.selection;
          const $from = sel.$from;
          const match = sel.empty && !$from.parent.type.spec.code
            ? NOTE_LINK_TRIGGER.exec($from.parent.textBetween(0, $from.parentOffset, undefined, '￼'))
            : null;
          if (!match) { dismissedAt = null; if (state) close(); return; }
          const from = $from.pos - match[0].length;
          if (from === dismissedAt) return;
          const query = match[1];
          const changed = !state || state.query !== query;
          state = { from, to: $from.pos, query };
          popup.hidden = false;
          position(view);
          if (changed) { render(); search(query); }
        },
        destroy: () => popup.remove(),
      }),
      props: {
        handleKeyDown: (_view, e) => {
          if (!state) return false;
          switch (e.key) {
            case 'ArrowDown': if (items.length) { selected = (selected + 1) % items.length; render(); } return true;
            case 'ArrowUp': if (items.length) { selected = (selected - 1 + items.length) % items.length; render(); } return true;
            case 'Enter': case 'Tab': if (items.length) { choose(selected); return true; } return false;
            case 'Escape': dismissedAt = state.from; close(); return true;
            default: return false;
          }
        },
        handleDOMEvents: { blur: () => { close(); return false; } },
      },
    })];
  },
});

// Clicking a link to another note opens it (Ctrl/Cmd/middle-click: new tab). Other links stay plain editable text.
function noteHrefAt(event) {
  const a = event.target instanceof Element ? event.target.closest('a[href]') : null;
  const href = a?.getAttribute('href') ?? '';
  return /^\/notes\/[0-9a-f-]{36}$/i.test(href) ? href : null;
}

// Tiptap writes empty paragraphs as "&nbsp;" lines to preserve them; in markdown they're just noise.
const cleanMarkdown = (md) => md.replace(/^&nbsp;[ \t]*$\n?/gm, '').replace(/\n{3,}/g, '\n\n');

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
 * @param {{debounceMs?: number, fileInput?: HTMLInputElement, canGenerate?: boolean}} options
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
    await dotnetRef.invokeMethodAsync('OnContentChanged', cleanMarkdown(editor.getMarkdown()));
  };
  // Always save (used before generating, where the latest text must be on the server).
  const flushNow = async () => { dirty = true; await flush(); };

  const editor = new Editor({
    element,
    extensions: [
      StarterKit.configure({ link: { openOnClick: false, autolink: true } }),
      Markdown,
      Placeholder.configure({
        placeholder: ({ node }) => node.type.name === 'aiRequest'
          ? 'What should Claude write here? e.g. “Summarise this chapter from my notes above”'
          : 'Start writing… type /ai to ask Claude, or paste a YouTube link or image',
      }),
      AiRequest.configure({
        // Save first so the server sees the request, then ask it to generate.
        onGenerate: options?.canGenerate
          ? (requestId) => flushNow()
            .then(() => dotnetRef.invokeMethodAsync('OnGenerate', requestId))
            .catch((e) => { setRequestStatus(editor, requestId, 'failed'); reportError(e); })
          : null,
      }),
      AiResult,
      NoteLinkPicker.configure({ findNotes: (q) => dotnetRef.invokeMethodAsync('FindNotes', q) }),
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
    editorProps: {
      handleDOMEvents: {
        click: (_view, event) => {
          const href = noteHrefAt(event);
          if (!href || event.button !== 0) return false;
          event.preventDefault();
          if (event.ctrlKey || event.metaKey) window.open(href, '_blank');
          else if (window.Blazor?.navigateTo) window.Blazor.navigateTo(href);
          else window.location.href = href;
          return true;
        },
        auxclick: (_view, event) => {
          const href = noteHrefAt(event);
          if (!href || event.button !== 1) return false;
          event.preventDefault();
          window.open(href, '_blank');
          return true;
        },
      },
    },
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
    aiRequest: () => editor.chain().focus().setNode('aiRequest', { id: newRequestId() }).run(),
  };

  return {
    flush,
    exec: (name, arg) => (commands[name] ? commands[name](arg) : false),
    setRequestStatus: (requestId, status) => setRequestStatus(editor, requestId, status),
    /** Applies an edit Claude made on the server. Returns false if it couldn't be applied in place. */
    applyRemote: (kind, args) => {
      try { return !!remoteEdits[kind]?.(editor, args); } catch (e) { console.error(e); return false; }
    },
    /** Fallback: replace the whole document, keeping the cursor roughly where it was. */
    setMarkdown: (markdown) => {
      const { from } = editor.state.selection;
      editor.commands.setContent(markdown, { contentType: 'markdown', emitUpdate: false });
      editor.commands.setTextSelection(Math.min(from, editor.state.doc.content.size));
      dirty = false;
    },
    getMarkdown: () => cleanMarkdown(editor.getMarkdown()),
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
    extensions: [StarterKit, Markdown, Image, YouTubeEmbed, TableKit, TaskList, TaskItem.configure({ nested: true }), AiRequest, AiResult],
    content: markdown,
    contentType: 'markdown',
  });
  const out = ed.getMarkdown();
  ed.destroy();
  return out;
}
