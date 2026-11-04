// Global keyboard shortcuts. Ctrl/Cmd+K opens the quick switcher (handled in MainLayout.razor).
export function register(dotnetRef) {
  const handler = (e) => {
    if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === 'k') {
      e.preventDefault();
      e.stopPropagation();
      dotnetRef.invokeMethodAsync('OpenQuickSwitcher');
    }
  };
  document.addEventListener('keydown', handler, true);
  return { dispose: () => document.removeEventListener('keydown', handler, true) };
}
