// Sidebar drag and drop. Shows where the dragged item will land (before / after / into a row)
// without a server round trip per mousemove, and reports the drop to NoteTree.DropAt.
// Rows carry data-id, data-folder (folders), data-open (expanded folders) and data-nodrop
// (the dragged item and everything inside it).
export function init(nav, dotnetRef) {
  let dragId = null, marked = null, zone = null;

  const clear = () => {
    if (marked) delete marked.dataset.drop;
    marked = null;
    zone = null;
  };
  const rowOf = (e) => e.target instanceof Element ? e.target.closest('.tree-row[data-id]') : null;

  const zoneFor = (row, e) => {
    const r = row.getBoundingClientRect();
    const y = (e.clientY - r.top) / r.height;
    if (row.dataset.folder === undefined) return y < 0.5 ? 'before' : 'after';
    if (y < 0.25) return 'before';
    // Below an open folder is its first child, so the lower part means "into".
    return y > 0.75 && row.dataset.open === undefined ? 'after' : 'into';
  };

  const onStart = (e) => { dragId = rowOf(e)?.dataset.id ?? null; };

  const onOver = (e) => {
    if (!dragId) return;
    const row = rowOf(e);
    if (row?.hasAttribute('data-nodrop')) { clear(); return; } // not allowed: leave the cursor as "no drop"
    e.preventDefault();
    const z = row ? zoneFor(row, e) : null;
    if (row === marked && z === zone) return;
    clear();
    if (row) { row.dataset.drop = z; marked = row; zone = z; }
  };

  const onLeave = (e) => { if (!nav.contains(e.relatedTarget)) clear(); };

  const onDrop = (e) => {
    if (!dragId) return;
    e.preventDefault();
    const row = rowOf(e);
    const id = dragId;
    const target = row ? row.dataset.id : null;
    const z = row ? zoneFor(row, e) : 'root';
    clear();
    dragId = null;
    if (row?.hasAttribute('data-nodrop')) return;
    dotnetRef.invokeMethodAsync('DropAt', id, target, z);
  };

  const onEnd = () => { clear(); dragId = null; };

  nav.addEventListener('dragstart', onStart);
  nav.addEventListener('dragover', onOver);
  nav.addEventListener('dragleave', onLeave);
  nav.addEventListener('drop', onDrop);
  nav.addEventListener('dragend', onEnd);
  return {
    dispose: () => {
      nav.removeEventListener('dragstart', onStart);
      nav.removeEventListener('dragover', onOver);
      nav.removeEventListener('dragleave', onLeave);
      nav.removeEventListener('drop', onDrop);
      nav.removeEventListener('dragend', onEnd);
    },
  };
}
