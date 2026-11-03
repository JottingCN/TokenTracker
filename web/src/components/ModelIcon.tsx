import { Tooltip } from 'antd';
import { providerOf } from '../utils/model';

const PROVIDER_STYLES: Record<string, { color: string; letter: string }> = {
  DeepSeek: { color: '#4D6BFE', letter: 'D' },
  OpenAI: { color: '#10A37F', letter: 'O' },
  Anthropic: { color: '#D97757', letter: 'A' },
  Google: { color: '#4285F4', letter: 'G' },
  Meta: { color: '#0866FF', letter: 'M' },
  Kimi: { color: '#111827', letter: 'K' },
  '小米 MiMo': { color: '#FF6900', letter: 'M' },
  'Xiaomi MiMo': { color: '#FF6900', letter: 'M' },
  MiniMax: { color: '#E5484D', letter: 'M' },
  '智谱 GLM': { color: '#3859FF', letter: 'G' },
  'Zhipu GLM': { color: '#3859FF', letter: 'G' },
};

interface ModelIconProps {
  model: string;
  size?: number;
  tooltip?: boolean;
}

export function ModelIcon({
  model,
  size = 16,
  tooltip = true,
}: ModelIconProps) {
  const provider = providerOf(model);
  const style = PROVIDER_STYLES[provider] ?? {
    color: '#8C8C8C',
    letter: '?',
  };
  const icon = (
    <span
      aria-label={provider}
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        justifyContent: 'center',
        flex: '0 0 auto',
        width: size,
        height: size,
        borderRadius: Math.max(3, Math.round(size * 0.25)),
        background: style.color,
        color: '#fff',
        fontSize: Math.max(9, Math.round(size * 0.58)),
        fontWeight: 600,
        lineHeight: 1,
      }}
    >
      {style.letter}
    </span>
  );

  return tooltip ? <Tooltip title={provider}>{icon}</Tooltip> : icon;
}
