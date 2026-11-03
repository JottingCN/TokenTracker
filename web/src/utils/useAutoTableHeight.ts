import { useLayoutEffect, useRef, useState } from 'react';

export function useAutoTableHeight(reservedPx = 150) {
  const ref = useRef<HTMLDivElement>(null);
  const [height, setHeight] = useState(360);

  useLayoutEffect(() => {
    const update = () => {
      const element = ref.current;
      if (!element) {
        return;
      }

      const top = element.getBoundingClientRect().top;
      const content = element.closest('.ant-layout-content');
      let bottom = window.innerHeight;
      if (content) {
        const rect = content.getBoundingClientRect();
        const paddingBottom =
          parseFloat(getComputedStyle(content).paddingBottom) || 0;
        bottom = rect.bottom - paddingBottom;
      }

      const next = Math.max(
        90,
        Math.floor(bottom - top - reservedPx),
      );
      setHeight((current) => (Math.abs(current - next) > 1 ? next : current));
    };

    update();
    window.addEventListener('resize', update);
    const observer = new ResizeObserver(update);
    if (ref.current) {
      observer.observe(ref.current);
    }
    return () => {
      window.removeEventListener('resize', update);
      observer.disconnect();
    };
  }, [reservedPx]);

  return [ref, height] as const;
}
