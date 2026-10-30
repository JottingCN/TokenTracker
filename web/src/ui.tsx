import { createContext, useContext, useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import zhCN from 'antd/locale/zh_CN';
import enUS from 'antd/locale/en_US';
import { setMoneyLanguage } from './utils/money';

export type Language = 'zh' | 'en';
export type LanguageMode = 'auto' | Language;
export type ThemeMode = 'auto' | 'light' | 'dark';

interface UiContextValue {
  lang: Language;
  languageMode: LanguageMode;
  setLanguageMode: (mode: LanguageMode) => void;
  themeMode: ThemeMode;
  setThemeMode: (mode: ThemeMode) => void;
  isDark: boolean;
  antdLocale: typeof zhCN;
  tr: (zh: string, en: string) => string;
}

const LANGUAGE_KEY = 'tokentracker.language';
const THEME_KEY = 'tokentracker.theme';

const UiContext = createContext<UiContextValue | null>(null);

let currentUiLanguage: Language = 'zh';

export function translate(zh: string, en: string): string {
  return currentUiLanguage === 'zh' ? zh : en;
}

function detectLanguage(): Language {
  const preferred = navigator.language || navigator.languages?.[0] || 'en';
  return preferred.toLowerCase().startsWith('zh') ? 'zh' : 'en';
}

function loadLanguageMode(): LanguageMode {
  try {
    const value = localStorage.getItem(LANGUAGE_KEY);
    if (value === 'zh' || value === 'en' || value === 'auto') {
      return value;
    }
  } catch {
    // Storage is best-effort.
  }
  return 'auto';
}

function loadThemeMode(): ThemeMode {
  try {
    const value = localStorage.getItem(THEME_KEY);
    if (value === 'light' || value === 'dark' || value === 'auto') {
      return value;
    }
  } catch {
    // Storage is best-effort.
  }
  return 'auto';
}

export function UiProvider({ children }: { children: ReactNode }) {
  const [languageMode, setLanguageMode] = useState<LanguageMode>(loadLanguageMode);
  const [themeMode, setThemeMode] = useState<ThemeMode>(loadThemeMode);
  const [systemDark, setSystemDark] = useState(
    () => window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false,
  );

  const lang: Language =
    languageMode === 'auto' ? detectLanguage() : languageMode;
  const isDark =
    themeMode === 'dark' ||
    (themeMode === 'auto' && systemDark);

  useEffect(() => {
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    const update = (event: MediaQueryListEvent) => setSystemDark(event.matches);
    media.addEventListener('change', update);
    return () => media.removeEventListener('change', update);
  }, []);

  useEffect(() => {
    try {
      localStorage.setItem(LANGUAGE_KEY, languageMode);
      localStorage.setItem(THEME_KEY, themeMode);
    } catch {
      // Storage is best-effort.
    }
  }, [languageMode, themeMode]);

  useEffect(() => {
    document.documentElement.lang = lang === 'zh' ? 'zh-CN' : 'en';
    document.documentElement.dataset.theme = isDark ? 'dark' : 'light';
    document.documentElement.style.colorScheme = isDark ? 'dark' : 'light';
  }, [lang, isDark]);

  currentUiLanguage = lang;
  setMoneyLanguage(lang);

  const value = useMemo<UiContextValue>(
    () => ({
      lang,
      languageMode,
      setLanguageMode,
      themeMode,
      setThemeMode,
      isDark,
      antdLocale: lang === 'zh' ? zhCN : enUS,
      tr: (zh, en) => (lang === 'zh' ? zh : en),
    }),
    [lang, languageMode, themeMode, isDark],
  );

  return <UiContext.Provider value={value}>{children}</UiContext.Provider>;
}

export function useUi(): UiContextValue {
  const value = useContext(UiContext);
  if (!value) {
    throw new Error('useUi must be used inside UiProvider');
  }
  return value;
}
