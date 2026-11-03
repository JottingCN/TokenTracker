import { useEffect, useState } from 'react';

export function useViewportHeight(
  ratio: number,
  minHeight: number,
  maxHeight: number,
) {
  const measure = () =>
    Math.max(
      minHeight,
      Math.min(maxHeight, Math.round(window.innerHeight * ratio)),
    );
  const [height, setHeight] = useState(measure);

  useEffect(() => {
    const update = () => setHeight(measure());
    window.addEventListener('resize', update);
    return () => window.removeEventListener('resize', update);
  }, [ratio, minHeight, maxHeight]);

  return height;
}
