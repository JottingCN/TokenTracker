// Row heights measured in the running app: single-line cells are 41px and cells
// that stack a token count over a cost need 48px. Table bodies are clamped to
// these numbers so a short list does not stretch to the full viewport.
export const TABLE_HEADER_PX = 39;
export const TABLE_ROW_SINGLE_PX = 41;
export const TABLE_ROW_DOUBLE_PX = 48;

// Keeps the body tall enough to look intentional but never taller than its rows:
// a one-row list should not reserve the whole viewport, which is also why the
// CSS `min-height` of `.tt-fill-table` receives this value.
export function clampTableHeight(
  availablePx: number,
  rows: number,
  rowPx: number = TABLE_ROW_DOUBLE_PX,
  emptyPx = 160,
): number {
  // +4 keeps a full page of rows from triggering a 1-2px overflow scrollbar.
  const content = TABLE_HEADER_PX + Math.max(0, rows) * rowPx + 4;
  return Math.max(
    0,
    Math.min(availablePx, rows > 0 ? content : emptyPx),
  );
}
