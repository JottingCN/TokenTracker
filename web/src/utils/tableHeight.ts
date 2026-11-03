const FOOTER_HEIGHT = 35;

export function tableHeight(offsetPx: number): string {
  return `max(120px, calc(100vh - ${offsetPx + FOOTER_HEIGHT}px))`;
}
