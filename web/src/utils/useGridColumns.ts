import { useLayoutEffect, useRef, useState } from 'react';

// Chooses the largest column count that fits, but avoids a lone card in the
// last row (e.g. 9 cards at max 4 columns become 3 + 3 + 3).
export function useGridColumns(
  count: number,
  minWidth = 170,
  gap = 12,
) {
  const ref = useRef<HTMLDivElement>(null);
  const [columns, setColumns] = useState(1);

  useLayoutEffect(() => {
    const element = ref.current;
    if (!element) {
      return;
    }

    const update = () => {
      const width = element.clientWidth;
      const max = Math.max(
        1,
        Math.floor((width + gap) / (minWidth + gap)),
      );
      let next = Math.min(count, max);
      if (next > 2 && count % next === 1) {
        for (let candidate = next - 1; candidate >= 2; candidate -= 1) {
          if (count % candidate !== 1) {
            next = candidate;
            break;
          }
        }
      }

      setColumns((current) => (current === next ? current : next));
    };

    update();
    const observer = new ResizeObserver(update);
    observer.observe(element);
    return () => observer.disconnect();
  }, [count, minWidth, gap]);

  return [ref, columns] as const;
}
