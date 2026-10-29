import { createContext, useContext, useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';

export type MoneyUnit = 'cny' | 'usd' | 'credit';

export interface MoneySettings {
  unit: MoneyUnit;
  usdRate: number;
  creditRate: number;
}

interface MoneyContextValue {
  settings: MoneySettings;
  setSettings: (patch: Partial<MoneySettings>) => void;
  unitLabel: string;
  priceSuffix: string;
  toPriceInput: (cny: number) => number;
  fromPriceInput: (value: number) => number;
  pricePrecision: number;
}

const STORAGE_KEY = 'tokentracker.money';
const DEFAULT_SETTINGS: MoneySettings = {
  unit: 'cny',
  usdRate: 7.2,
  creditRate: 0.01,
};

let current = DEFAULT_SETTINGS;

function normalize(value: Partial<MoneySettings> | null): MoneySettings {
  const unit =
    value?.unit === 'usd' || value?.unit === 'credit' ? value.unit : 'cny';
  const usdRate =
    typeof value?.usdRate === 'number' &&
    Number.isFinite(value.usdRate) &&
    value.usdRate > 0
      ? value.usdRate
      : DEFAULT_SETTINGS.usdRate;
  const creditRate =
    typeof value?.creditRate === 'number' &&
    Number.isFinite(value.creditRate) &&
    value.creditRate > 0
      ? value.creditRate
      : DEFAULT_SETTINGS.creditRate;
  return { unit, usdRate, creditRate };
}

function load(): MoneySettings {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? normalize(JSON.parse(raw) as Partial<MoneySettings>) : DEFAULT_SETTINGS;
  } catch {
    return DEFAULT_SETTINGS;
  }
}

current = typeof localStorage === 'undefined' ? DEFAULT_SETTINGS : load();

export function unitLabel(unit: MoneyUnit = current.unit): string {
  if (unit === 'usd') {
    return '美元（USD）';
  }
  if (unit === 'credit') {
    return '积分';
  }
  return '人民币（CNY）';
}

export function formatMoney(
  cny: number | null | undefined,
  digits?: number,
): string {
  if (cny === null || cny === undefined || !Number.isFinite(cny)) {
    return '-';
  }

  if (current.unit === 'usd') {
    return `$${(cny / current.usdRate).toFixed(digits ?? 6)}`;
  }
  if (current.unit === 'credit') {
    return `${(cny / current.creditRate).toFixed(digits ?? 2)} 积分`;
  }
  return `¥${cny.toFixed(digits ?? 6)}`;
}

export function formatCost(
  cny: number | null | undefined,
  unpricedCalls = 0,
): string {
  const formatted = formatMoney(cny);
  return unpricedCalls > 0
    ? `${formatted} + ${unpricedCalls} 未定价`
    : formatted;
}

export function formatPrice(cny: number | null | undefined): string {
  return formatMoney(cny, current.unit === 'credit' ? 2 : 4);
}

function toPriceInput(cny: number): number {
  if (current.unit === 'usd') {
    return cny / current.usdRate;
  }
  if (current.unit === 'credit') {
    return cny / current.creditRate;
  }
  return cny;
}

function fromPriceInput(value: number): number {
  if (current.unit === 'usd') {
    return value * current.usdRate;
  }
  if (current.unit === 'credit') {
    return value * current.creditRate;
  }
  return value;
}

const MoneyContext = createContext<MoneyContextValue | null>(null);

export function MoneyProvider({ children }: { children: ReactNode }) {
  const [settings, setSettingsState] = useState<MoneySettings>(load);
  current = settings;

  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
    } catch {
      // Storage is best-effort.
    }
  }, [settings]);

  const value = useMemo<MoneyContextValue>(
    () => ({
      settings,
      setSettings: (patch) =>
        setSettingsState((previous) => normalize({ ...previous, ...patch })),
      unitLabel: unitLabel(settings.unit),
      priceSuffix: '/ 百万 tokens',
      toPriceInput,
      fromPriceInput,
      pricePrecision: settings.unit === 'credit' ? 2 : 4,
    }),
    [settings],
  );

  return <MoneyContext.Provider value={value}>{children}</MoneyContext.Provider>;
}

export function useMoney(): MoneyContextValue {
  const value = useContext(MoneyContext);
  if (!value) {
    throw new Error('useMoney must be used inside MoneyProvider');
  }
  return value;
}
