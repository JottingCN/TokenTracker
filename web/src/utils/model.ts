import { translate } from '../ui';

const PROVIDERS: Array<[RegExp, string]> = [
  [/^deepseek/i, 'DeepSeek'],
  [/^(openai|gpt|o[134])/i, 'OpenAI'],
  [/^(anthropic|claude)/i, 'Anthropic'],
  [/^(google|gemini)/i, 'Google'],
  [/^(meta|llama)/i, 'Meta'],
  [/^kimi/i, 'Kimi'],
  [/^mimo/i, '小米 MiMo'],
  [/^minimax/i, 'MiniMax'],
  [/^glm/i, '智谱 GLM'],
];

function leafOf(model: string): string {
  const slash = model.lastIndexOf('/');
  return slash >= 0 ? model.slice(slash + 1) : model;
}

export function providerOf(model: string): string {
  const leaf = leafOf(model);
  const provider =
    PROVIDERS.find(([pattern]) => pattern.test(leaf))?.[1] ?? '其他';
  if (provider === '小米 MiMo') {
    return translate('小米 MiMo', 'Xiaomi MiMo');
  }
  if (provider === '智谱 GLM') {
    return translate('智谱 GLM', 'Zhipu GLM');
  }
  if (provider === '其他') {
    return translate('其他', 'Other');
  }
  return provider;
}

export function shortModelName(model: string): string {
  return leafOf(model)
    .replace(/^(deepseek|openai|anthropic|google|meta|kimi|mimo|minimax|glm)[-/:]/i, '')
    .split(/[-_]/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ');
}

export function sessionDisplay(title: string | null, sessionId: string): string {
  if (title && title.trim()) {
    return title;
  }

  const short = sessionId.length > 20 ? `${sessionId.slice(0, 8)}…` : sessionId;
  return `未命名 · ${short}`;
}
