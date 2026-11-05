import * as vscode from 'vscode';
import { toTraditional } from './zhConvert';

// Locale tags match the web dashboard (and VS Code language identifiers) so a
// zh-tw user gets Traditional Chinese instead of the English fallback.
export type Locale = 'en' | 'zh-CN' | 'zh-TW';

const LANGUAGE_IDS: ReadonlyArray<readonly [Locale, readonly string[]]> = [
  ['en', ['en']],
  ['zh-CN', ['zh-cn', 'zh-hans', 'zh']],
  ['zh-TW', ['zh-tw', 'zh-hant', 'zh-hk', 'zh-mo']],
];

export function resolveLocale(tag: string | null | undefined): Locale {
  const normalized = (tag ?? '').trim().toLowerCase().replace(/_/g, '-');
  if (!normalized) {
    return 'en';
  }
  for (const [id, aliases] of LANGUAGE_IDS) {
    if (aliases.includes(normalized)) {
      return id;
    }
  }
  const parts = normalized.split('-');
  for (let length = parts.length; length > 0; length -= 1) {
    const candidate = parts.slice(0, length).join('-');
    for (const [id, aliases] of LANGUAGE_IDS) {
      if (aliases.includes(candidate)) {
        return id;
      }
    }
  }
  return 'en';
}

export const locale: Locale = resolveLocale(vscode.env.language);

/**
 * Picks the message for the active locale. `traditional` is optional: without it
 * the simplified text is converted, which covers short labels well enough.
 */
export function tr(chinese: string, english: string, traditional?: string): string {
  if (locale === 'zh-CN') {
    return chinese;
  }
  if (locale === 'zh-TW') {
    return traditional ?? toTraditional(chinese);
  }
  return english;
}
