export function tableHeight(offsetPx: number): string {
  return `max(220px, calc(100vh - ${offsetPx}px))`;
}
