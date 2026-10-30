import * as vscode from 'vscode';
import { tr } from './i18n';

export type CostUnit = 'cny' | 'usd' | 'credit';

export interface MoneySettings {
  unit: CostUnit;
  usdRate: number;
  creditRate: number;
}

export function readMoneySettings(): MoneySettings {
  const config = vscode.workspace.getConfiguration('tokentracker');
  const unit = config.get<string>('costUnit', 'cny');
  return {
    unit: unit === 'usd' || unit === 'credit' ? unit : 'cny',
    usdRate: Math.max(0.01, config.get<number>('usdRate', 7.2)),
    creditRate: Math.max(0.0001, config.get<number>('creditRate', 0.01)),
  };
}

export function formatMoney(
  cny: number,
  settings: MoneySettings,
  digits = 6,
): string {
  if (settings.unit === 'usd') {
    return `$${(cny / settings.usdRate).toFixed(digits)}`;
  }
  if (settings.unit === 'credit') {
    return `${(cny / settings.creditRate).toFixed(2)} ${unitName(settings.unit)}`;
  }
  return `¥${cny.toFixed(digits)}`;
}

export function formatCost(
  cny: number,
  unpricedCalls: number,
  settings: MoneySettings,
): string {
  const formatted = formatMoney(cny, settings);
  if (unpricedCalls <= 0) {
    return formatted;
  }
  return `${formatted} + ${unpricedCalls} ${tr('次未定价', 'unpriced')}`;
}

export function unitName(unit: CostUnit): string {
  if (unit === 'usd') {
    return 'USD';
  }
  if (unit === 'credit') {
    return tr('积分', 'credits');
  }
  return 'CNY';
}
